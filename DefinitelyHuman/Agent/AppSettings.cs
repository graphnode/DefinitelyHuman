using System.Collections.Concurrent;
using DefinitelyHuman.Data;

namespace DefinitelyHuman.Agent;

/// <summary>
/// Settings changed from the dashboard and kept in the database: which model each job uses and
/// which memory jobs are switched on. Everything reads through here on each use, so a change
/// applies to the next model call without a restart.
/// </summary>
public sealed class AppSettings
{
    public const string ChatModel = "model.chat";
    public const string ExtractionModel = "model.extraction";
    public const string CurationModel = "model.curation";
    public const string DescriptionModel = "model.description";
    public const string ExtractionEnabled = "memory.extraction";
    public const string CurationEnabled = "memory.curation";
    public const string RecallEnabled = "memory.recall";
    public const string DescriptionEnabled = "memory.description";

    private readonly Dictionary<string, string> _defaults;
    private readonly ConcurrentDictionary<string, string> _values = new();

    /// <param name="chatModel">Default chat model (from ANTHROPIC_MODEL), used until one is set in the dashboard.</param>
    public AppSettings(string chatModel)
    {
        _defaults = new Dictionary<string, string>
        {
            [ChatModel] = chatModel,
            [ExtractionModel] = "claude-haiku-4-5-20251001",
            [CurationModel] = "claude-sonnet-5-5",
            [DescriptionModel] = "claude-haiku-4-5-20251001",
            [ExtractionEnabled] = "true",
            [CurationEnabled] = "true",
            [RecallEnabled] = "true",
            [DescriptionEnabled] = "false", // spends on web searches, so it is asked for
        };

        using var db = new ChattingContext();
        foreach (var setting in db.Settings)
            _values[setting.Key] = setting.Value;
    }

    public string Get(string key) => _values.TryGetValue(key, out string? value) ? value : _defaults[key];

    public bool IsOn(string key) => Get(key) == "true";

    public async Task SetAsync(string key, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            value = _defaults[key];

        await using var db = new ChattingContext();
        if (await db.Settings.FindAsync(key) is { } existing)
            existing.Value = value;
        else
            db.Settings.Add(new Setting { Key = key, Value = value });
        await db.SaveChangesAsync();
        _values[key] = value;
    }
}
