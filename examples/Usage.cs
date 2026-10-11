using ContextWindow;

namespace ContextWindow.Tests;

internal static class Usage
{
    internal static Report Empty()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Ledger ledger = new("example", start, start.AddHours(1), [], new Dictionary<string, Tariff> { ["A"] = new("illustrative", 1m, 2m, 0m) });
        return ledger.CreateReport("scripted");
    }
}
