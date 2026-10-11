using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextWindow;

/// <summary>Frozen expected refund and deadline.</summary>
public sealed record RefundCase(string Tenant, string Id, string Order, bool Eligible, decimal Amount, string Currency, DateTimeOffset Deadline);

/// <summary>USD per million tokens, with cached tokens included in input.</summary>
public sealed record Tariff(string Date, decimal Input, decimal Output, decimal Cached);

/// <summary>A single model attempt, including unknown usage.</summary>
public sealed record ModelCall(string Configuration, string Run, string Provider, string Model, string Id,
    string Tenant, string Case, string Step, DateTimeOffset At, int? Input, int? Output, int? Cached,
    string Provenance, string Status, double? DurationMs);

/// <summary>A separately priced local tool invocation.</summary>
public sealed record ToolCall(string Configuration, string Run, string Id, string Tenant, string Case,
    string Step, DateTimeOffset At, decimal FeeUsd);

/// <summary>Authoritative local service record, not the agent's proposed answer.</summary>
public sealed record Outcome(string Configuration, string Run, string Operation, string Tenant, string Case,
    string Order, bool Refused, decimal? CommittedAmount, string? CommittedCurrency, bool Authorized, DateTimeOffset At);

/// <summary>Execution identity and observable run status.</summary>
public sealed record RunRecord(string Configuration, string Run, string Tenant, string Case,
    DateTimeOffset Started, DateTimeOffset? Ended, string Status);

/// <summary>Priced call and its original usage evidence.</summary>
public sealed record PricedCall(ModelCall Call, decimal? Usd);

/// <summary>Totals grouped by expensive workflow step.</summary>
public sealed record StepReport(string Step, int ModelCalls, int ToolCalls, int? Input, int? Output,
    int? Cached, int UnknownCalls, decimal KnownModelUsd, decimal ToolUsd);

/// <summary>Every declared case, including ones never attempted.</summary>
public sealed record CaseReport(RefundCase Expected, string Configuration, Outcome? Observed, string Status,
    IReadOnlyList<RunRecord> Runs, IReadOnlyList<PricedCall> Calls, IReadOnlyList<ToolCall> Tools,
    IReadOnlyList<StepReport> Steps, decimal KnownModelUsd, decimal ToolUsd, bool CostComplete, double? CompletionMs);

/// <summary>Configuration-level sums and explicit denominators.</summary>
public sealed record ConfigurationReport(string Configuration, Tariff Tariff, int Cases, int Runs,
    int ModelCalls, int ToolCalls, int Correct, int Incorrect, int Unresolved, decimal ResolutionRate,
    decimal? TerminalCorrectnessRate, int CompletionSamples, double? MeanCompletionMs, int? Input,
    int? Output, int? Cached, int UnknownCalls, decimal KnownModelUsd, decimal ToolUsd, bool CostComplete,
    decimal? CostPerCorrectUsd, string CostStatus, IReadOnlyList<CaseReport> CaseRows);

/// <summary>All configurations evaluated on one fixed cohort and UTC window.</summary>
public sealed record Report(string Cohort, string Mode, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<ConfigurationReport> Configurations);

/// <summary>Application-owned accounting independent of trace export.</summary>
public sealed class Ledger
{
    private readonly string _cohort;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;
    private readonly RefundCase[] _cases;
    private readonly Dictionary<string, Tariff> _tariffs;
    private readonly Dictionary<(string Configuration, string Run), RunRecord> _runs = [];
    private readonly Dictionary<(string Configuration, string Run, string Provider, string Id), ModelCall> _models = [];
    private readonly Dictionary<(string Configuration, string Run, string Id), ToolCall> _tools = [];
    private readonly Dictionary<(string Configuration, string Case), Outcome> _outcomes = [];
    private readonly Dictionary<(string Configuration, string Operation), string> _operations = [];

    /// <summary>Validate a frozen cohort, UTC window and dated price inputs.</summary>
    public Ledger(string cohort, DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<RefundCase> cases, IReadOnlyDictionary<string, Tariff> tariffs)
    {
        if (string.IsNullOrWhiteSpace(cohort) || start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero
            || start >= end || tariffs.Count == 0
            || cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count
            || cases.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Tenant)
                || string.IsNullOrWhiteSpace(item.Order) || string.IsNullOrWhiteSpace(item.Currency)
                || item.Amount <= 0 || item.Deadline < start)
            || tariffs.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value.Date)
                || pair.Value.Input < 0 || pair.Value.Output < 0 || pair.Value.Cached < 0))
        {
            throw new ArgumentException("Invalid frozen cohort, window or tariff.");
        }

        _cohort = cohort;
        _start = start;
        _end = end;
        _cases = cases.ToArray();
        _tariffs = new Dictionary<string, Tariff>(tariffs);
    }

    /// <summary>Bind one run identity permanently to one case within a configuration.</summary>
    public void AddRun(string configuration, string caseId, string run, DateTimeOffset? started = null)
    {
        RequireConfiguration(configuration);
        RefundCase item = RequireCase(caseId);
        DateTimeOffset at = started ?? _start;
        RequireWindow(at);

        if (string.IsNullOrWhiteSpace(run))
        {
            throw new ArgumentException("Run identity required.");
        }

        var key = (configuration, run);

        if (_runs.TryGetValue(key, out RunRecord? previous))
        {
            if (previous.Case != caseId || previous.Tenant != item.Tenant || previous.Started != at)
            {
                throw new InvalidOperationException("Run ownership conflict.");
            }

            return;
        }

        _runs.Add(key, new RunRecord(configuration, run, item.Tenant, caseId, at, null, "pending"));
    }

    /// <summary>Finish execution without asserting a business outcome.</summary>
    public void FinishRun(string configuration, string run, DateTimeOffset at, string status)
    {
        RequireWindow(at);

        if (!_runs.TryGetValue((configuration, run), out RunRecord? previous) || at < previous.Started)
        {
            throw new InvalidOperationException("Unknown run or invalid finish time.");
        }

        RunRecord finished = previous with
        {
            Ended = at,
            Status = status
        };

        if (previous.Ended is not null && previous != finished)
        {
            throw new InvalidOperationException("Conflicting run completion.");
        }

        _runs[(configuration, run)] = finished;
    }

    /// <summary>Count identical repeated exports once and reject conflicting duplicates.</summary>
    public void AddModel(ModelCall call)
    {
        RequireWindow(call.At);
        RequireOwner(call.Configuration, call.Run, call.Case, call.Tenant);

        if (string.IsNullOrWhiteSpace(call.Provider) || string.IsNullOrWhiteSpace(call.Model)
            || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Step)
            || string.IsNullOrWhiteSpace(call.Provenance) || call.Input is < 0 || call.Output is < 0
            || call.Cached is < 0 || call.Cached > call.Input || call.DurationMs is < 0)
        {
            throw new ArgumentException("Invalid model usage or call identity.");
        }

        var key = (call.Configuration, call.Run, call.Provider, call.Id);

        if (_models.TryGetValue(key, out ModelCall? previous))
        {
            if (previous != call)
            {
                throw new InvalidOperationException("Conflicting duplicate model call.");
            }

            return;
        }

        _models.Add(key, call);
    }

    /// <summary>Record a separately priced tool attempt.</summary>
    public void AddTool(ToolCall call)
    {
        RequireWindow(call.At);
        RequireOwner(call.Configuration, call.Run, call.Case, call.Tenant);

        if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Step) || call.FeeUsd < 0)
        {
            throw new ArgumentException("Invalid tool attempt.");
        }

        var key = (call.Configuration, call.Run, call.Id);

        if (_tools.TryGetValue(key, out ToolCall? previous))
        {
            if (previous != call)
            {
                throw new InvalidOperationException("Conflicting duplicate tool call.");
            }

            return;
        }

        _tools.Add(key, call);
    }

    /// <summary>Register immutable operation ownership before executing a business action.</summary>
    public void AddOperation(string configuration, string caseId, string operation)
    {
        RequireConfiguration(configuration);
        _ = RequireCase(caseId);
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Original operation identity required.");
        }
        var key = (configuration, operation);
        if (_operations.TryGetValue(key, out string? owner) && owner != caseId)
        {
            throw new InvalidOperationException("Operation ownership conflict.");
        }
        _operations[key] = caseId;
    }

    /// <summary>Reuse the first observation metadata for repeated exports, including recreated wrappers.</summary>
    public ModelCall NormalizeReplay(ModelCall call)
    {
        return _models.TryGetValue((call.Configuration, call.Run, call.Provider, call.Id), out ModelCall? previous)
            ? call with
            {
                At = previous.At,
                DurationMs = previous.DurationMs
            }
            : call;
    }

    /// <summary>Join a terminal service record to its original operation identity.</summary>
    public void AddOutcome(Outcome outcome)
    {
        RequireWindow(outcome.At);
        RequireOwner(outcome.Configuration, outcome.Run, outcome.Case, outcome.Tenant);

        if (string.IsNullOrWhiteSpace(outcome.Operation))
        {
            throw new ArgumentException("Original operation identity required.");
        }

        var operationKey = (outcome.Configuration, outcome.Operation);

        if (!_operations.TryGetValue(operationKey, out string? owner) || owner != outcome.Case)
        {
            throw new InvalidOperationException("Operation ownership conflict.");
        }

        var key = (outcome.Configuration, outcome.Case);

        if (_outcomes.TryGetValue(key, out Outcome? previous))
        {
            if (previous != outcome)
            {
                throw new InvalidOperationException("Conflicting terminal outcome.");
            }

            return;
        }

        _outcomes.Add(key, outcome);
    }

    /// <summary>Include every attempted-case charge and every declared case in rates.</summary>
    public Report CreateReport(string mode)
    {
        List<ConfigurationReport> configurations = [];

        foreach ((string configuration, Tariff tariff) in _tariffs.OrderBy(pair => pair.Key))
        {
            List<CaseReport> rows = [];

            foreach (RefundCase item in _cases)
            {
                ModelCall[] calls = _models.Values.Where(call => call.Configuration == configuration && call.Case == item.Id).ToArray();
                ToolCall[] tools = _tools.Values.Where(tool => tool.Configuration == configuration && tool.Case == item.Id).ToArray();
                RunRecord[] runs = _runs.Values.Where(run => run.Configuration == configuration && run.Case == item.Id).ToArray();
                _outcomes.TryGetValue((configuration, item.Id), out Outcome? outcome);
                string status = outcome is null ? "unresolved" : Correct(item, outcome) ? "correct" : "incorrect";
                PricedCall[] priced = calls.Select(call => new PricedCall(call, Price(call, tariff))).ToArray();
                StepReport[] steps = calls.Select(call => call.Step).Concat(tools.Select(tool => tool.Step))
                    .Distinct().Order().Select(step =>
                    {
                        ModelCall[] models = calls.Where(call => call.Step == step).ToArray();
                        ToolCall[] selectedTools = tools.Where(tool => tool.Step == step).ToArray();
                        return new StepReport(step, models.Length, selectedTools.Length,
                            Sum(models.Select(call => call.Input)), Sum(models.Select(call => call.Output)),
                            Sum(models.Select(call => call.Cached)), models.Count(call => Price(call, tariff) is null),
                            models.Sum(call => Price(call, tariff) ?? 0m), selectedTools.Sum(tool => tool.FeeUsd));
                    }).ToArray();

                rows.Add(new CaseReport(item, configuration, outcome, status, runs, priced, tools, steps,
                    steps.Sum(step => step.KnownModelUsd), steps.Sum(step => step.ToolUsd),
                    priced.All(call => call.Usd.HasValue),
                    outcome is null ? null : (outcome.At - _start).TotalMilliseconds));
            }

            int correct = rows.Count(row => row.Status == "correct");
            int incorrect = rows.Count(row => row.Status == "incorrect");
            bool complete = rows.All(row => row.CostComplete);
            double[] durations = rows.Where(row => row.Status == "correct")
                .Select(row => row.CompletionMs!.Value).ToArray();
            ModelCall[] allCalls = _models.Values.Where(call => call.Configuration == configuration).ToArray();
            decimal modelUsd = rows.Sum(row => row.KnownModelUsd);
            decimal toolUsd = rows.Sum(row => row.ToolUsd);

            configurations.Add(new ConfigurationReport(configuration, tariff, rows.Count,
                rows.Sum(row => row.Runs.Count), rows.Sum(row => row.Calls.Count),
                rows.Sum(row => row.Tools.Count), correct, incorrect, rows.Count - correct - incorrect,
                rows.Count == 0 ? 0m : (decimal)correct / rows.Count,
                correct + incorrect == 0 ? null : (decimal)correct / (correct + incorrect),
                durations.Length, durations.Length == 0 ? null : durations.Average(),
                Sum(allCalls.Select(call => call.Input)), Sum(allCalls.Select(call => call.Output)),
                Sum(allCalls.Select(call => call.Cached)),
                rows.Sum(row => row.Steps.Sum(step => step.UnknownCalls)), modelUsd, toolUsd, complete,
                correct > 0 && complete ? (modelUsd + toolUsd) / correct : null,
                !complete ? "missing_usage" : correct == 0 ? "no_correct_resolutions" : "priced", rows));
        }

        return new Report(_cohort, mode, _start, _end, configurations);
    }

    /// <summary>Persist standard JSON and quoted CSV.</summary>
    public static async Task WriteAsync(Report report, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        await using (FileStream file = File.Create(Path.Combine(directory, "report.json")))
        {
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "report.csv"), ToCsv(report), cancellationToken);
    }

    /// <summary>CSV case records include nested run, call, tool and step details.</summary>
    public static string ToCsv(Report report)
    {
        StringBuilder csv = new();
        csv.AppendLine("cohort,mode,start,end,configuration,tariff,tenant,case,status,runs,knownModelUsd,toolUsd,costComplete,observed,calls,tools,steps");

        foreach (ConfigurationReport configuration in report.Configurations)
        {
            foreach (CaseReport row in configuration.CaseRows)
            {
                string[] cells =
                [
                    report.Cohort, report.Mode, report.Start.ToString("O"), report.End.ToString("O"),
                    configuration.Configuration, JsonSerializer.Serialize(configuration.Tariff),
                    row.Expected.Tenant, row.Expected.Id, row.Status, JsonSerializer.Serialize(row.Runs),
                    row.KnownModelUsd.ToString(CultureInfo.InvariantCulture),
                    row.ToolUsd.ToString(CultureInfo.InvariantCulture), row.CostComplete.ToString(),
                    JsonSerializer.Serialize(row.Observed), JsonSerializer.Serialize(row.Calls),
                    JsonSerializer.Serialize(row.Tools), JsonSerializer.Serialize(row.Steps)
                ];

                csv.AppendLine(string.Join(",", cells.Select(Escape)));
            }
        }

        return csv.ToString();
    }

    private static string Escape(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static decimal? Price(ModelCall call, Tariff tariff)
    {
        if (call.Input is null || call.Output is null || call.Cached is null)
        {
            return null;
        }

        return ((call.Input.Value - call.Cached.Value) * tariff.Input
            + call.Cached.Value * tariff.Cached + call.Output.Value * tariff.Output) / 1_000_000m;
    }

    private static int? Sum(IEnumerable<int?> values)
    {
        int total = 0;

        foreach (int? value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            total = checked(total + value.Value);
        }

        return total;
    }

    private static bool Correct(RefundCase item, Outcome outcome)
    {
        bool valid = outcome.Tenant == item.Tenant && outcome.Order == item.Order
            && outcome.Authorized && outcome.At <= item.Deadline;

        return valid && (item.Eligible
            ? !outcome.Refused && outcome.CommittedAmount == item.Amount
                && outcome.CommittedCurrency == item.Currency
            : outcome.Refused && outcome.CommittedAmount is null && outcome.CommittedCurrency is null);
    }

    private RefundCase RequireCase(string id)
    {
        return _cases.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("Unknown cohort case.");
    }

    private void RequireConfiguration(string configuration)
    {
        if (!_tariffs.ContainsKey(configuration))
        {
            throw new InvalidOperationException("Unknown configuration.");
        }
    }

    private void RequireOwner(string configuration, string run, string caseId, string tenant)
    {
        RequireConfiguration(configuration);
        RefundCase item = RequireCase(caseId);

        if (item.Tenant != tenant || !_runs.TryGetValue((configuration, run), out RunRecord? owner)
            || owner.Tenant != tenant || owner.Case != caseId)
        {
            throw new InvalidOperationException("Unknown or mismatched run, tenant or case.");
        }
    }

    private void RequireWindow(DateTimeOffset at)
    {
        if (at < _start || at >= _end)
        {
            throw new InvalidOperationException("Outside frozen observation window [start, end).");
        }
    }
}
