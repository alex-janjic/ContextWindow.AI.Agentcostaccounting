using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace ContextWindow.Tests;

[Xunit.Collection("Report execution")]
public sealed class PipelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RefundCase Case = new("tenant", "eligible-1", "order", true, 10m, "USD", Start.AddHours(1));

    [Xunit.Fact]
    public async Task RealAgentsEmitClientAndAgentSpansAndPersistReconciledReports()
    {
        List<Activity> spans = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "ContextWindow.AgentCostAccounting",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        await Program.Main([]);
        Xunit.Assert.Contains(spans, span => span.TagObjects.Any(tag => tag.Key == "gen_ai.operation.name" && Equals(tag.Value, "chat")));
        Xunit.Assert.Contains(spans, span => span.TagObjects.Any(tag => tag.Key == "gen_ai.operation.name" && Equals(tag.Value, "invoke_agent")));
        using System.Text.Json.JsonDocument report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync("artifacts/report.json"));
        System.Text.Json.JsonElement configurations = report.RootElement.GetProperty("Configurations");
        Xunit.Assert.Equal(2, configurations.GetArrayLength());
        Xunit.Assert.Equal(3, configurations[0].GetProperty("ModelCalls").GetInt32());
        Xunit.Assert.Equal(2, configurations[0].GetProperty("Correct").GetInt32());
        Xunit.Assert.Equal(1, configurations[1].GetProperty("Incorrect").GetInt32());
        Xunit.Assert.Equal(0.00054m, configurations[0].GetProperty("KnownModelUsd").GetDecimal());
        Xunit.Assert.True(File.Exists("artifacts/report.csv"));
    }

    [Xunit.Fact]
    public async Task WrapperExtractsUsageReturnsSameResponseAndDeduplicatesReplay()
    {
        Ledger ledger = Create();
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "APPROVE"))
        {
            ResponseId = "provider-response",
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                CachedInputTokenCount = 40
            }
        };
        using AccountingChatClient wrapper = new(new FixtureClient(response), ledger, "A", "run", Case, "scripted", "model", true);
        Xunit.Assert.Same(response, await wrapper.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]));
        await wrapper.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]);
        using AccountingChatClient recreated = new(new FixtureClient(response), ledger, "A", "run", Case, "scripted", "model", true);
        await recreated.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]);
        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.Equal(1, row.ModelCalls);
        Xunit.Assert.Equal(0.00018m, row.KnownModelUsd);
        response.Usage.InputTokenCount = 101;
        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task FailedOrMissingUsageRemainsUnknownAndUnresolved(bool fails)
    {
        Ledger ledger = Create();
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "APPROVE"));
        using AccountingChatClient wrapper = new(new FixtureClient(response, fails), ledger, "A", "run", Case, "scripted", "model", true);
        if (fails)
        {
            await Xunit.Assert.ThrowsAsync<HttpRequestException>(() => wrapper.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]));
        }
        else
        {
            await wrapper.GetResponseAsync([new ChatMessage(ChatRole.User, "refund")]);
        }

        ConfigurationReport row = ledger.CreateReport("scripted").Configurations[0];
        Xunit.Assert.Equal(1, row.ModelCalls);
        Xunit.Assert.Equal(1, row.UnknownCalls);
        Xunit.Assert.Equal(1, row.Unresolved);
        Xunit.Assert.False(row.CostComplete);
        Xunit.Assert.Null(row.CostPerCorrectUsd);
    }

    private static Ledger Create()
    {
        Ledger ledger = new("cohort", Start, Start.AddHours(1), [Case], new Dictionary<string, Tariff> { ["A"] = new("illustrative", 1m, 2m, 0.5m) });
        ledger.AddRun("A", Case.Id, "run");
        return ledger;
    }

    private sealed class FixtureClient(ChatResponse response, bool fails = false) : IChatClient
    {
        public ChatClientMetadata Metadata => new("fixture");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => fails ? Task.FromException<ChatResponse>(new HttpRequestException("fixture failure")) : Task.FromResult(response);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }
}
