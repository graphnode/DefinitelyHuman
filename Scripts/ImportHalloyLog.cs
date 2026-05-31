#!/usr/bin/env dotnet run
#:package Microsoft.Data.Sqlite@10.0.8

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run Scripts/ImportHalloyLog.cs -- <halloy-log.json.gz> [--clear] [--channel <override>] [--nick <bot-nick>]");
    return 1;
}

string gzPath = args[0];
if (!File.Exists(gzPath))
{
    Console.Error.WriteLine($"File not found: {gzPath}");
    return 1;
}

var channelOverride = "#clankersunite";
var botNick = "DefinitelyHuman";
var clearFirst = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--clear":
            clearFirst = true;
            break;
        case "--channel" when i + 1 < args.Length:
            channelOverride = args[++i];
            break;
        case "--nick" when i + 1 < args.Length:
            botNick = args[++i];
            break;
    }
}

await using var stream = File.OpenRead(gzPath);
await using var gz = new GZipStream(stream, CompressionMode.Decompress);

List<HalloyEntry> entries = JsonSerializer.Deserialize(gz, HalloyJsonContext.Default.ListHalloyEntry)!;
Console.WriteLine($"Parsed {entries.Count} entries from halloy log");

string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string dbPath = Path.Join(folder, "chatting.db");
Console.WriteLine($"Database: {dbPath}");

await using var conn = new SqliteConnection($"Data Source={dbPath}");
await conn.OpenAsync();

await using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Messages (
            MessageId INTEGER PRIMARY KEY AUTOINCREMENT,
            Channel TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            Nick TEXT NOT NULL,
            Text TEXT NOT NULL,
            IsOwnMessage INTEGER NOT NULL DEFAULT 0
        )
        """;
    await cmd.ExecuteNonQueryAsync();
}

if (clearFirst)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        -- noinspection SqlWithoutWhereForFile
        DELETE FROM Messages
    """;
    int deleted = await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"Cleared {deleted} existing messages");
}

await using var tx = await conn.BeginTransactionAsync();
await using var insert = conn.CreateCommand();
insert.CommandText = """
    INSERT INTO Messages (Channel, Timestamp, Nick, Text, IsOwnMessage)
    VALUES ($channel, $timestamp, $nick, $text, $isOwn)
    """;
var pChannel = insert.Parameters.Add("$channel", SqliteType.Text);
var pTimestamp = insert.Parameters.Add("$timestamp", SqliteType.Text);
var pNick = insert.Parameters.Add("$nick", SqliteType.Text);
var pText = insert.Parameters.Add("$text", SqliteType.Text);
var pIsOwn = insert.Parameters.Add("$isOwn", SqliteType.Integer);
insert.Prepare();

int imported = 0, skipped = 0;

foreach (var entry in entries)
{
    if (entry.Target?.Channel is not { } ch)
    {
        skipped++;
        continue;
    }

    string? nick = ExtractNick(ch.Source);
    if (nick is null)
    {
        skipped++;
        continue;
    }

    string? channel = channelOverride ?? ch.ChannelInfo?.Raw ?? ch.ChannelInfo?.Normalized;
    if (channel is null)
    {
        skipped++;
        continue;
    }

    string? text = entry.Text;
    if (string.IsNullOrWhiteSpace(text))
    {
        skipped++;
        continue;
    }

    bool isOwn = botNick is not null
        && string.Equals(nick, botNick, StringComparison.OrdinalIgnoreCase);

    pChannel.Value = channel;
    pTimestamp.Value = entry.ServerTime.ToString("o");
    pNick.Value = nick;
    pText.Value = text;
    pIsOwn.Value = isOwn ? 1 : 0;
    await insert.ExecuteNonQueryAsync();
    imported++;
}

await tx.CommitAsync();
Console.WriteLine($"Imported {imported} messages, skipped {skipped}");
return 0;

static string? ExtractNick(JsonElement? source)
{
    if (source is not { } s) return null;
    if (s.ValueKind != JsonValueKind.Object) return null;
    if (!s.TryGetProperty("User", out var userEl)) return null;

    string? raw = userEl.GetString();
    if (raw is null) return null;

    string trimmed = raw.TrimStart('@', '+', ' ');
    int bang = trimmed.IndexOf('!');
    return bang > 0 ? trimmed[..bang] : trimmed;
}

public class HalloyEntry
{

    [JsonPropertyName("server_time")] public DateTime ServerTime { get; set; }
    [JsonPropertyName("direction")] public string? Direction { get; set; }
    [JsonPropertyName("target")] public HalloyTarget? Target { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public class HalloyTarget
{
    [JsonPropertyName("Channel")] public HalloyChannel? Channel { get; set; }
}

public class HalloyChannel
{
    [JsonPropertyName("channel")] public HalloyChannelInfo? ChannelInfo { get; set; }
    [JsonPropertyName("source")] public JsonElement? Source { get; set; }
}

public class HalloyChannelInfo
{
    [JsonPropertyName("normalized")] public string? Normalized { get; set; }
    [JsonPropertyName("raw")] public string? Raw { get; set; }
}


[JsonSerializable(typeof(List<HalloyEntry>))]
internal partial class HalloyJsonContext : JsonSerializerContext;
