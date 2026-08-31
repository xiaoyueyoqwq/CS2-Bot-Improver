using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BotBanter;

public sealed class Line
{
    [JsonPropertyName("t")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("flag")] public string? Flag { get; set; }
}

public sealed class LexiconFile
{
    [JsonPropertyName("scenarios")] public Dictionary<string, Dictionary<string, List<Line>>> Scenarios { get; set; } = new();
    [JsonPropertyName("cheer")] public List<Line> Cheer { get; set; } = new();
    [JsonPropertyName("awards")] public Dictionary<string, List<Line>> Awards { get; set; } = new();
    [JsonPropertyName("mvp_praise")] public List<Line> MvpPraise { get; set; } = new();
    [JsonPropertyName("mvp_bot")] public List<Line> MvpBot { get; set; } = new();
}

public sealed class Lexicon
{
    private static readonly Regex Placeholder = new(@"\{[a-z_]+\}", RegexOptions.Compiled);

    private LexiconFile _data = new();
    private readonly HashSet<string> _usedThisMap = new();
    private readonly Random _rng = new();

    public bool Loaded { get; private set; }
    public int TotalLines { get; private set; }

    public void Load(string moduleDirectory)
    {
        var path = Path.Combine(moduleDirectory, "lines", "zh-CN.json");
        var text = File.ReadAllText(path);
        _data = JsonSerializer.Deserialize<LexiconFile>(text) ?? new LexiconFile();
        TotalLines = _data.Scenarios.Sum(s => s.Value.Sum(p => p.Value.Count))
                     + _data.Cheer.Count
                     + _data.Awards.Sum(a => a.Value.Count)
                     + _data.MvpPraise.Count + _data.MvpBot.Count;
        Loaded = true;
    }

    public void ResetMap() => _usedThisMap.Clear();

    // Picks a scenario line for a persona, honoring per-map dedupe and
    // full template fact-fill (军规6/8). Returns null when nothing valid is available.
    public PickedLine? Pick(string scenario, Persona persona, Dictionary<string, string> vars)
    {
        if (!_data.Scenarios.TryGetValue(scenario, out var byPersona))
        {
            return null;
        }

        var tag = PersonaTraits.Tag(persona);
        if (!byPersona.TryGetValue(tag, out var lines) || lines.Count == 0)
        {
            return null;
        }

        var candidates = lines
            .Where(l => !_usedThisMap.Contains(l.Text))
            .ToList();
        Shuffle(candidates);

        foreach (var line in candidates)
        {
            var filled = Fill(line.Text, vars);
            if (filled == null)
            {
                continue; // 军规6: missing data -> drop the line, never fabricate
            }

            _usedThisMap.Add(line.Text);
            return new PickedLine(filled, line.Flag);
        }

        return null;
    }

    public PickedLine? PickCheer()
    {
        var candidates = _data.Cheer.Where(l => !_usedThisMap.Contains(l.Text)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var line = candidates[_rng.Next(candidates.Count)];
        _usedThisMap.Add(line.Text);
        return new PickedLine(line.Text, null);
    }

    public string? PickAward(string award, Dictionary<string, string> vars)
    {
        if (!_data.Awards.TryGetValue(award, out var lines) || lines.Count == 0)
        {
            return null;
        }

        var shuffled = lines.ToList();
        Shuffle(shuffled);
        foreach (var line in shuffled)
        {
            var filled = Fill(line.Text, vars);
            if (filled != null)
            {
                return filled;
            }
        }

        return null;
    }

    public string PickMvpPraise(bool mvpIsBot)
    {
        var pool = mvpIsBot && _data.MvpBot.Count > 0 ? _data.MvpBot : _data.MvpPraise;
        return pool.Count == 0 ? string.Empty : pool[_rng.Next(pool.Count)].Text;
    }

    public static string? Fill(string template, Dictionary<string, string> vars)
    {
        var result = Placeholder.Replace(template, m =>
        {
            var key = m.Value[1..^1];
            return vars.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : m.Value;
        });

        return Placeholder.IsMatch(result) ? null : result;
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

public sealed record PickedLine(string Text, string? Flag);
