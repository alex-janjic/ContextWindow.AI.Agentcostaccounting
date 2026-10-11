using System.Diagnostics;
using System.ClientModel;
using OpenAI;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ContextWindow;

/// <summary>Run two real agents against the same fictional refund cases.</summary>
public static class Program
{
    private const string TraceSource = "ContextWindow.AgentCostAccounting";

    /// <summary>Print and persist the complete case comparison.</summary>
    public static async Task Main(string[] args)
    {
        bool live = args.Length > 0 && args[0] == "live";
        LiveOptions? options = live ? LiveOptions.FromEnvironment() : null;
        using TracerProvider traces = Sdk.CreateTracerProviderBuilder().AddSource(TraceSource).Build();
        DateTimeOffset start = live ? DateTimeOffset.UtcNow.AddMinutes(-1)
            : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(1);
        RefundCase[] cases = Enumerable.Range(1, options?.Repetitions ?? 1).SelectMany(index => new[]
        {
            new RefundCase("fictional-tenant", $"eligible-{index}", $"order-e-{index}", true, 10m, "USD", end),
            new RefundCase("fictional-tenant", $"ineligible-{index}", $"order-i-{index}", false, 10m, "USD", end),
            new RefundCase("fictional-tenant", $"pending-{index}", $"order-p-{index}", true, 10m, "USD", end)
        }).ToArray();
        IReadOnlyDictionary<string, Tariff> tariffs = options?.Tariffs
            ?? new Dictionary<string, Tariff>
            {
                ["A"] = new("illustrative-2026-01-01", 1m, 2m, 0.5m),
                ["B"] = new("illustrative-2026-01-01", 1m, 2m, 0.5m)
            };
        Ledger ledger = new("fictional-refunds", start, end, cases, tariffs);

        foreach (string configuration in tariffs.Keys.Order())
        {
            foreach (RefundCase item in cases)
            {
                string run = $"{configuration}-{item.Id}";
                ledger.AddRun(configuration, item.Id, run, live ? DateTimeOffset.UtcNow : start);
                ledger.AddOperation(configuration, item.Id, $"operation-{configuration}-{item.Id}");
                string model = options?.Model(configuration) ?? $"scripted-{configuration}";
                IChatClient inner = options is null
                    ? new ScriptedChatClient(item, configuration)
                    : new OpenAIClient(new ApiKeyCredential(options.Key),
                        new OpenAIClientOptions { Endpoint = options.Endpoint })
                        .GetChatClient(model).AsIChatClient();
                using AccountingChatClient accounting = new(inner, ledger, configuration, run, item,
                    options?.Provider ?? "scripted", model, !live);
                using IChatClient client = accounting.AsBuilder()
                    .UseOpenTelemetry(sourceName: TraceSource, configure: config => config.EnableSensitiveData = false)
                    .Build();
                AIAgent agent = client.AsAIAgent(name: $"refund-{configuration}", instructions:
                    "Reply only REFUSE or APPROVE. Do not claim a refund was committed.")
                    .AsBuilder()
                    .UseOpenTelemetry(sourceName: TraceSource, configure: config => config.EnableSensitiveData = false)
                    .Build();
                string status = "completed";

                try
                {
                    AgentResponse response = await agent.RunAsync(
                        $"Policy: eligible={item.Eligible}; order={item.Order}. Decide refund.");
                    LocalRefundService service = new(ledger, live ? DateTimeOffset.UtcNow : start.AddMinutes(1));
                    service.Apply(configuration, run, item, response.Text.Trim().ToUpperInvariant(),
                        item.Id.StartsWith("pending-", StringComparison.Ordinal));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or ClientResultException)
                {
                    status = "failed_or_unknown";
                }

                ledger.FinishRun(configuration, run, live ? DateTimeOffset.UtcNow : start.AddMinutes(1), status);
            }
        }

        Report report = ledger.CreateReport(live ? "live-local-outcomes" : "scripted");
        await Ledger.WriteAsync(report, "artifacts");
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(Ledger.ToCsv(report));
    }
}

internal sealed class LocalRefundService(Ledger ledger, DateTimeOffset at)
{
    public void Apply(string configuration, string run, RefundCase item, string decision, bool hold)
    {
        ledger.AddTool(new ToolCall(configuration, run, "policy-1", item.Tenant, item.Id, "policy", at, 0m));

        if (hold || (decision != "REFUSE" && decision != "APPROVE"))
        {
            return;
        }

        bool refused = decision == "REFUSE";
        bool authorized = refused || item.Eligible;
        ledger.AddOutcome(new Outcome(configuration, run, $"operation-{configuration}-{item.Id}",
            item.Tenant, item.Id, item.Order, refused,
            authorized && !refused ? item.Amount : null,
            authorized && !refused ? item.Currency : null, authorized, at));
    }
}

internal sealed record LiveOptions(Uri Endpoint, string Key, string Provider, string ModelA,
    string ModelB, IReadOnlyDictionary<string, Tariff> Tariffs, int Repetitions)
{
    public string Model(string configuration)
    {
        return configuration == "A" ? ModelA : ModelB;
    }

    public static LiveOptions FromEnvironment()
    {
        string[] required =
        [
            "COST_ENDPOINT", "COST_API_KEY", "COST_PROVIDER", "COST_MODEL_A",
            "COST_MODEL_B", "COST_TARIFF_DATE", "COST_A_INPUT", "COST_A_CACHED",
            "COST_A_OUTPUT", "COST_B_INPUT", "COST_B_CACHED", "COST_B_OUTPUT",
            "COST_REPETITIONS"
        ];
        string[] missing = required.Where(name =>
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException("Missing live configuration: " + string.Join(", ", missing));
        }

        string Get(string name)
        {
            return Environment.GetEnvironmentVariable(name)!;
        }

        if (!Uri.TryCreate(Get("COST_ENDPOINT"), UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("COST_ENDPOINT must be HTTPS.");
        }

        if (Get("COST_MODEL_A") == Get("COST_MODEL_B"))
        {
            throw new ArgumentException("Distinct model identities required.");
        }

        int repetitions = int.Parse(Get("COST_REPETITIONS"), System.Globalization.CultureInfo.InvariantCulture);

        if (repetitions is < 1 or > 10 || !DateOnly.TryParse(Get("COST_TARIFF_DATE"), out _))
        {
            throw new ArgumentException("Invalid repetition count or tariff date.");
        }

        decimal Rate(string name)
        {
            return decimal.Parse(Get(name), System.Globalization.CultureInfo.InvariantCulture);
        }

        string date = Get("COST_TARIFF_DATE");
        Dictionary<string, Tariff> tariffs = new()
        {
            ["A"] = new(date, Rate("COST_A_INPUT"), Rate("COST_A_OUTPUT"), Rate("COST_A_CACHED")),
            ["B"] = new(date, Rate("COST_B_INPUT"), Rate("COST_B_OUTPUT"), Rate("COST_B_CACHED"))
        };
        return new LiveOptions(endpoint, Get("COST_API_KEY"), Get("COST_PROVIDER"),
            Get("COST_MODEL_A"), Get("COST_MODEL_B"), tariffs, repetitions);
    }
}


internal sealed class AccountingChatClient(
    IChatClient inner, Ledger ledger, string configuration, string run,
    RefundCase item, string provider, string model, bool scripted) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string attempt = Guid.NewGuid().ToString("N");
        Stopwatch timer = Stopwatch.StartNew();
        ChatResponse response;

        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch
        {
            timer.Stop();
            ledger.AddModel(new ModelCall(configuration, run, provider, model, attempt,
                item.Tenant, item.Id, "decision", Now(), null, null, null,
                scripted ? "scripted synthetic fixture" : "provider usage unavailable",
                "failed_or_unknown", scripted ? 10d : timer.Elapsed.TotalMilliseconds));
            throw;
        }

        timer.Stop();
        string id = string.IsNullOrWhiteSpace(response.ResponseId) ? attempt : response.ResponseId;
        UsageDetails? usage = response.Usage;
        ModelCall call = new(configuration, run, provider, response.ModelId ?? model, id,
            item.Tenant, item.Id, "decision", Now(), Count(usage?.InputTokenCount),
            Count(usage?.OutputTokenCount), Count(usage?.CachedInputTokenCount),
            scripted ? "scripted synthetic fixture; cache included in input"
                : "provider SDK UsageDetails; cache included in input",
            "ok", scripted ? 10d : timer.Elapsed.TotalMilliseconds);

        call = ledger.NormalizeReplay(call);
        ledger.AddModel(call);
        return response;
    }

    private DateTimeOffset Now() => scripted
        ? new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero)
        : DateTimeOffset.UtcNow;

    private static int? Count(long? value) => value.HasValue ? checked((int)value.Value) : null;
}

internal sealed class ScriptedChatClient(RefundCase item, string configuration) : IChatClient
{
    public ChatClientMetadata Metadata => new("scripted");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string decision = item.Eligible && !(configuration == "B"
            && item.Id.StartsWith("eligible-", StringComparison.Ordinal)) ? "APPROVE" : "REFUSE";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, decision))
        {
            ResponseId = Guid.NewGuid().ToString("N"),
            ModelId = $"scripted-{configuration}",
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                CachedInputTokenCount = 40
            }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose()
    {
    }
}
