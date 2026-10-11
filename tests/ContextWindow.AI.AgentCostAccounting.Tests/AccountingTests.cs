using ContextWindow;

namespace ContextWindow.Tests;

[Xunit.Collection("Report execution")]
public sealed class AccountingTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RefundCase Eligible = new("tenant", "eligible", "order-1", true, 10m, "USD", Start.AddMinutes(30));
    private static readonly RefundCase Ineligible = new("tenant", "ineligible", "order-2", false, 10m, "USD", Start.AddMinutes(30));

    [Xunit.Fact]
    public void ThreeAttemptsCountOnceAndRetainAllSpending()
    {
        Ledger ledger = Create();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string run = $"run-{attempt}";
            ledger.AddRun("A", Eligible.Id, run);
            ModelCall call = Charge(run, Eligible);
            ledger.AddModel(call);
            ledger.AddModel(call);
        }

        ledger.AddOutcome(Result(Eligible, "run-0", false));
        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.Equal(3, row.Runs);
        Xunit.Assert.Equal(3, row.ModelCalls);
        Xunit.Assert.Equal(1, row.Correct);
        Xunit.Assert.Equal(1, row.Unresolved);
        Xunit.Assert.Equal(0.00054m, row.CostPerCorrectUsd);
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOutcome(Result(Eligible, "run-1", true)));
    }

    [Xunit.Fact]
    public void PendingAndIncorrectRefusalRetainSpendWithoutCorrectResolution()
    {
        Ledger ledger = Create();
        ledger.AddRun("A", Eligible.Id, "one");
        ledger.AddModel(Charge("one", Eligible));
        ledger.AddOutcome(Result(Eligible, "one", true));
        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.Equal(1, row.Incorrect);
        Xunit.Assert.Equal(1, row.Unresolved);
        Xunit.Assert.Equal(0.00018m, row.KnownModelUsd);
        Xunit.Assert.Null(row.CostPerCorrectUsd);
        Xunit.Assert.Equal("no_correct_resolutions", row.CostStatus);
        Xunit.Assert.Equal(2, ledger.CreateReport("scripted").Configurations[1].Unresolved);
    }

    [Xunit.Fact]
    public void EligibleSuccessAndIneligibleNoWriteRefusalAreCorrect()
    {
        Ledger ledger = Create();
        ledger.AddRun("A", Eligible.Id, "one");
        ledger.AddRun("A", Ineligible.Id, "two");
        ledger.AddOutcome(Result(Eligible, "one", false));
        ledger.AddOutcome(Result(Ineligible, "two", true) with
        {
            Operation = "second"
        });
        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.Equal(2, row.Correct);
        Xunit.Assert.Equal(1m, row.ResolutionRate);
    }

    [Xunit.Fact]
    public void ConflictingDuplicatesOwnersAndLateEventsFail()
    {
        Ledger ledger = Create();
        ledger.AddRun("A", Eligible.Id, "one");
        ledger.AddRun("A", Eligible.Id, "one");
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddRun("A", Ineligible.Id, "one"));
        ModelCall call = Charge("one", Eligible);
        ledger.AddModel(call);
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddModel(call with { Input = 201 }));
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddModel(call with { Case = Ineligible.Id }));
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddModel(call with { At = Start.AddHours(1) }));
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOutcome(Result(Eligible, "one", false) with { At = Start.AddHours(1) }));
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOutcome(Result(Eligible, "one", false) with { Configuration = "unknown" }));
        ledger.AddOutcome(Result(Eligible, "one", false));
        ledger.AddRun("A", Ineligible.Id, "two");
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOutcome(Result(Ineligible, "two", true)));
    }

    [Xunit.Fact]
    public void MissingOrInvalidUsageDoesNotBecomeFree()
    {
        Ledger ledger = Create();
        ledger.AddRun("A", Eligible.Id, "one");
        Xunit.Assert.Throws<ArgumentException>(() => ledger.AddModel(Charge("one", Eligible) with { Cached = 101 }));
        Xunit.Assert.Throws<ArgumentException>(() => ledger.AddModel(Charge("one", Eligible) with { Input = -1 }));
        Xunit.Assert.Throws<ArgumentException>(() => new Ledger("bad", Start, Start.AddHours(1), [], new Dictionary<string, Tariff> { ["A"] = new("date", -1m, 0m, 0m) }));
        ledger.AddModel(Charge("one", Eligible) with
        {
            Output = null,
            Status = "failed_or_unknown"
        });
        ledger.AddOutcome(Result(Eligible, "one", false));
        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.False(row.CostComplete);
        Xunit.Assert.Equal(1, row.UnknownCalls);
        Xunit.Assert.Null(row.CostPerCorrectUsd);
        Xunit.Assert.Equal("missing_usage", row.CostStatus);
    }

    [Xunit.Fact]
    public void ResourceCurrencyAmountAndDeadlineMustMatch()
    {
        foreach (string difference in new[]
        {
            "order",
            "currency",
            "amount",
            "deadline",
            "refused_with_payment"
        }

        )
        {
            Ledger ledger = Create();
            RefundCase item = difference == "refused_with_payment" ? Ineligible : Eligible;
            ledger.AddRun("A", item.Id, "one");
            Outcome outcome = Result(item, "one", difference == "refused_with_payment");
            if (item.Id == Ineligible.Id)
            {
                outcome = outcome with
                {
                    Operation = "second"
                };
            }

            outcome = difference switch
            {
                "order" => outcome with
                {
                    Order = "other"
                },
                "currency" => outcome with
                {
                    CommittedCurrency = "EUR"
                },
                "amount" => outcome with
                {
                    CommittedAmount = 11m
                },
                "deadline" => outcome with
                {
                    At = Start.AddMinutes(31)
                },
                _ => outcome with
                {
                    CommittedAmount = 10m
                }
            };
            ledger.AddOutcome(outcome);
            Xunit.Assert.Equal(1, ledger.CreateReport("scripted").Configurations[0].Incorrect);
        }
    }

    [Xunit.Fact]
    public void PerStepChargesAndCsvEscapingReconcile()
    {
        RefundCase quoted = Eligible with
        {
            Id = "case,\"quoted\""
        };
        Ledger ledger = new("cohort", Start, Start.AddHours(1), [quoted], new Dictionary<string, Tariff> { ["A"] = new("illustrative", 1m, 2m, 0.5m) });
        ledger.AddRun("A", quoted.Id, "one");
        ledger.AddModel(Charge("one", quoted));
        ledger.AddTool(new ToolCall("A", "one", "tool", quoted.Tenant, quoted.Id, "policy", Start, 0.25m));
        ConfigurationReport row = Xunit.Assert.Single(ledger.CreateReport("scripted").Configurations);
        CaseReport item = Xunit.Assert.Single(row.CaseRows);
        Xunit.Assert.Equal(0.00018m, Xunit.Assert.Single(item.Calls).Usd);
        Xunit.Assert.Equal(0.25018m, item.Steps.Sum(step => step.KnownModelUsd + step.ToolUsd));
        Xunit.Assert.Equal(0.25018m, row.KnownModelUsd + row.ToolUsd);
        Xunit.Assert.Contains("\"case,\"\"quoted\"\"\"", Ledger.ToCsv(ledger.CreateReport("scripted")));
    }

    [Xunit.Fact]
    public async Task AgentExecutionAndServiceGiveUsefulAndBlockedControlsAsync()
    {
        await Program.Main([]);
        string json = await File.ReadAllTextAsync(Path.Combine("artifacts", "report.json"));
        Report? report = System.Text.Json.JsonSerializer.Deserialize<Report>(json);
        Xunit.Assert.NotNull(report);
        Xunit.Assert.Equal(2, report.Configurations[0].Correct);
        Xunit.Assert.Equal(1, report.Configurations[1].Correct);
        Xunit.Assert.Equal(1, report.Configurations[1].Incorrect);
        Xunit.Assert.Equal(1, report.Configurations[0].Unresolved);
        Xunit.Assert.Equal(0.00027m, report.Configurations[0].CostPerCorrectUsd);
        Xunit.Assert.Equal(0.00054m, report.Configurations[1].CostPerCorrectUsd);
        Xunit.Assert.True(File.Exists(Path.Combine("artifacts", "report.csv")));
    }

    [Xunit.Fact]
    public void OperationsAreRegisteredBeforeOutcomesAndCannotMoveBetweenCases()
    {
        Ledger ledger = Create();
        ledger.AddRun("A", Eligible.Id, "one");
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOutcome(Result(Eligible, "one", false) with { Operation = "unregistered" }));
        Xunit.Assert.Throws<InvalidOperationException>(() => ledger.AddOperation("A", Ineligible.Id, "operation-1"));
        ledger.AddOperation("A", Eligible.Id, "operation-1");
        ledger.AddOutcome(Result(Eligible, "one", false));
        Xunit.Assert.Equal(1, ledger.CreateReport("scripted").Configurations[0].Correct);
    }

    private static Ledger Create()
    {
        Ledger ledger = new("cohort", Start, Start.AddHours(1), [Eligible, Ineligible], new Dictionary<string, Tariff> { ["A"] = new("illustrative", 1m, 2m, 0.5m), ["B"] = new("illustrative", 1m, 2m, 0.5m) });
        ledger.AddOperation("A", Eligible.Id, "operation-1");
        ledger.AddOperation("A", Ineligible.Id, "second");
        return ledger;
    }

    private static ModelCall Charge(string run, RefundCase item)
    {
        return new ModelCall("A", run, "scripted", "scripted-A", "call", item.Tenant, item.Id, "decision", Start, 100, 50, 40, "synthetic", "ok", 1);
    }

    private static Outcome Result(RefundCase item, string run, bool refused)
    {
        return new Outcome("A", run, "operation-1", item.Tenant, item.Id, item.Order, refused, refused ? null : item.Amount, refused ? null : item.Currency, true, Start);
    }
}
