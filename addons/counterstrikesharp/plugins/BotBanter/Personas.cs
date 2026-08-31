using System.Text.Json;

namespace BotBanter;

public enum Persona
{
    Baozao,      // 暴躁老哥
    Yinyang,     // 阴阳怪气
    Gaoleng,     // 高冷职业哥
    Lezi,        // 乐子人
    Dashu,       // 慈祥大叔
    Xiaoxuesheng,// 破防小学生
    Laoyinbi     // 老阴哔
}

public static class PersonaTraits
{
    public static string Tag(Persona p) => p switch
    {
        Persona.Baozao => "暴躁",
        Persona.Yinyang => "阴阳",
        Persona.Gaoleng => "高冷",
        Persona.Lezi => "乐子",
        Persona.Dashu => "大叔",
        Persona.Xiaoxuesheng => "小学生",
        Persona.Laoyinbi => "老阴哔",
        _ => "暴躁"
    };

    // Base probability of staying silent even when the threshold is met.
    public static double SilenceRate(Persona p) => p switch
    {
        Persona.Gaoleng => 0.70,
        Persona.Laoyinbi => 0.60,
        Persona.Dashu => 0.40,
        Persona.Yinyang => 0.30,
        Persona.Lezi => 0.25,
        Persona.Baozao => 0.25,
        Persona.Xiaoxuesheng => 0.25,
        _ => 0.30
    };

    // 军规7: 高冷永不捧哏
    public static bool CanCheer(Persona p) => p != Persona.Gaoleng;

    // 军规7: 老阴哔被杀永不吭声
    public static bool SpeaksWhenKilled(Persona p) => p != Persona.Laoyinbi;
}

public sealed class PersonaAssigner
{
    private readonly Dictionary<string, Persona> _byBotName = new();
    private readonly HashSet<string> _proNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng = new();
    private readonly List<Persona> _rotation = new();

    public PersonaAssigner(string moduleDirectory)
    {
        LoadProNames(moduleDirectory);
    }

    public void Reset() => _byBotName.Clear();

    public Persona Get(string botName)
    {
        if (_byBotName.TryGetValue(botName, out var persona))
        {
            return persona;
        }

        // Pro-named bots (BotRandomizer / bot_info.json HLTV names) lean cold-pro.
        if (_proNames.Contains(botName) && _rng.NextDouble() < 0.6)
        {
            persona = _rng.NextDouble() < 0.7 ? Persona.Gaoleng : Persona.Yinyang;
        }
        else
        {
            if (_rotation.Count == 0)
            {
                _rotation.AddRange(Enum.GetValues<Persona>());
                for (var i = _rotation.Count - 1; i > 0; i--)
                {
                    var j = _rng.Next(i + 1);
                    (_rotation[i], _rotation[j]) = (_rotation[j], _rotation[i]);
                }
            }

            persona = _rotation[^1];
            _rotation.RemoveAt(_rotation.Count - 1);
        }

        _byBotName[botName] = persona;
        return persona;
    }

    private void LoadProNames(string moduleDirectory)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(moduleDirectory, "..", "..", "..", "BotHider", "bot_info.json"));
            if (!File.Exists(path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("players", out var players))
            {
                return;
            }

            foreach (var entry in players.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("player_name", out var name) && name.GetString() is { Length: > 0 } n)
                {
                    _proNames.Add(n);
                }
            }
        }
        catch
        {
            // Pro-name linkage is best effort; personas still work without it.
        }
    }
}
