using DefinitelyHuman.Data;

namespace DefinitelyHuman.Agent;

/// <summary>
/// The bot's own account of what it spends: one row per model call, priced from a table of list
/// prices. An estimate for the dashboard, not the bill.
/// </summary>
public static class UsageLog
{
    public const string Chat = "chat";
    public const string Extraction = "extraction";
    public const string Curation = "curation";
    public const string Lookup = "lookup";
    public const string Ask = "ask";

    // ponytail: list prices in dollars per million tokens (input, output), matched on the start
    // of the model id. Edited by hand when a price changes or a model is added; a model that is
    // not here shows as unpriced on the Usage page.
    private static readonly (string Prefix, decimal Input, decimal Output)[] Prices =
    [
        ("claude-haiku-4-5", 1m, 5m),
        ("claude-sonnet-5-5", 2m, 10m),
        ("claude-opus-5-5", 4m, 20m),
        ("claude-fable-5-1", 10m, 50m),
    ];

    private const decimal DollarsPerWebSearch = 0.01m;

    /// <summary>What a call cost in dollars at list price, or null when the model has no known price.</summary>
    public static decimal? Cost(string model, long inputTokens, long outputTokens, int webSearches)
    {
        foreach (var (prefix, input, output) in Prices)
        {
            if (model.StartsWith(prefix, StringComparison.Ordinal))
                return (inputTokens * input + outputTokens * output) / 1_000_000m + webSearches * DollarsPerWebSearch;
        }
        return null;
    }

    /// <summary>Records one model call. Never throws: bookkeeping must not break the call it counts.</summary>
    public static async Task RecordAsync(string job, string model, long inputTokens, long outputTokens, int webSearches = 0)
    {
        try
        {
            await using var db = new ChattingContext();
            db.ModelUsage.Add(new ModelUsage
            {
                Job = job, Model = model, InputTokens = inputTokens, OutputTokens = outputTokens, WebSearches = webSearches,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception)
        {
            // A missed row only makes the estimate a little low.
        }
    }
}
