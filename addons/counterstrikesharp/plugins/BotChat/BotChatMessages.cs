using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BotChat;

public sealed class BotChatMessages
{
    public List<string> Start { get; set; } = [];
    public List<string> HalfTime { get; set; } = [];
    public Dictionary<string, int> MatchEnd { get; set; } = [];
    public BotChatTauntMessages Taunts { get; set; } = new();
    public List<string> NiceShot { get; set; } = [];
    public List<string> Headshot { get; set; } = [];
    public List<string> ThroughSmoke { get; set; } = [];
    public List<string> Wallbang { get; set; } = [];
    public List<string> BlindKill { get; set; } = [];
    public List<string> AirborneKill { get; set; } = [];
    public List<string> Thanks { get; set; } = [];
}

public sealed class BotChatTauntMessages
{
    public List<string> DominantWin { get; set; } = [];
    public List<string> HalfTime { get; set; } = [];
    public List<string> AfterWin { get; set; } = [];
}

public static partial class BotChatMessageLoader
{
    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex LanguageNamePattern();

    public static BotChatMessages Load(string moduleDirectory, string language)
    {
        string normalizedLanguage = language?.Trim() ?? "";
        if (!LanguageNamePattern().IsMatch(normalizedLanguage))
            throw new InvalidDataException($"Invalid BotChat language name '{language}'.");

        string path = Path.Combine(moduleDirectory, "lang", $"{normalizedLanguage}.yml");
        if (!File.Exists(path))
            throw new FileNotFoundException($"BotChat language file was not found: {path}", path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var messages = deserializer.Deserialize<BotChatMessages>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"BotChat language file is empty: {path}");

        Normalize(messages.Start, "start", path);
        Normalize(messages.HalfTime, "half_time", path);
        Normalize(messages.Taunts.DominantWin, "taunts.dominant_win", path);
        Normalize(messages.Taunts.HalfTime, "taunts.half_time", path);
        Normalize(messages.Taunts.AfterWin, "taunts.after_win", path);
        Normalize(messages.NiceShot, "nice_shot", path);
        Normalize(messages.Headshot, "headshot", path);
        Normalize(messages.ThroughSmoke, "through_smoke", path);
        Normalize(messages.Wallbang, "wallbang", path);
        Normalize(messages.BlindKill, "blind_kill", path);
        Normalize(messages.AirborneKill, "airborne_kill", path);
        Normalize(messages.Thanks, "thanks", path);

        if (messages.MatchEnd.Count == 0)
            throw new InvalidDataException($"BotChat message pool 'match_end' is empty in {path}.");
        foreach (var (message, weight) in messages.MatchEnd)
        {
            if (string.IsNullOrWhiteSpace(message) || weight <= 0)
                throw new InvalidDataException($"BotChat 'match_end' entries require non-empty text and a positive weight in {path}.");
        }

        return messages;
    }

    private static void Normalize(List<string> messages, string poolName, string path)
    {
        if (messages.Count == 0)
            throw new InvalidDataException($"BotChat message pool '{poolName}' is empty in {path}.");

        for (int i = 0; i < messages.Count; i++)
        {
            string message = messages[i]?.Trim() ?? "";
            if (message.Length == 0)
                throw new InvalidDataException($"BotChat message pool '{poolName}' contains an empty entry in {path}.");
            messages[i] = message;
        }
    }
}
