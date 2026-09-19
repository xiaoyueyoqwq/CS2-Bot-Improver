// Banter support systems: personas, grudge memory and highlight scoring.
// These feed the banter reactions wired into BanterPlugin; message text and
// probabilities stay in the lang/*.yml pools and the plugin config.

namespace Banter;

public enum Persona
{
    Baozao,       // 暴躁老哥
    Yinyang,      // 阴阳怪气
    Gaoleng,      // 高冷职业哥
    Lezi,         // 乐子人
    Dashu,        // 慈祥大叔
    Xiaoxuesheng, // 破防小学生
    Laoyinbi      // 老阴哔
}

public static class PersonaTraits
{
    // Base probability of staying silent even when a banter trigger fires.
    public static double SilenceRate(Persona p) => p switch
    {
        Persona.Gaoleng => 0.60,
        Persona.Laoyinbi => 0.50,
        Persona.Dashu => 0.30,
        Persona.Yinyang => 0.20,
        Persona.Lezi => 0.15,
        Persona.Baozao => 0.15,
        Persona.Xiaoxuesheng => 0.15,
        _ => 0.25
    };

    // 老阴哔 never complains about its own death.
    public static bool SpeaksWhenKilled(Persona p) => p != Persona.Laoyinbi;
}

// Assigns a stable persona per bot for the duration of a map, using a
// shuffled rotation so a full lobby gets a varied cast.
public sealed class PersonaAssigner
{
    private readonly Dictionary<ulong, Persona> _bySteamId = new();
    private readonly List<Persona> _rotation = new();

    public void Reset()
    {
        _bySteamId.Clear();
        _rotation.Clear();
    }

    public Persona Get(ulong steamId)
    {
        if (_bySteamId.TryGetValue(steamId, out var persona))
            return persona;

        if (_rotation.Count == 0)
        {
            _rotation.AddRange(Enum.GetValues<Persona>());
            for (int i = _rotation.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_rotation[i], _rotation[j]) = (_rotation[j], _rotation[i]);
            }
        }

        persona = _rotation[^1];
        _rotation.RemoveAt(_rotation.Count - 1);
        _bySteamId[steamId] = persona;
        return persona;
    }
}

public sealed class GrudgeEdge
{
    public int Kills;
    public double Heat;
}

// (killer, victim) SteamID pairs -> kill memory. Map-scoped; red-hot state
// (a victim tilted at their repeat killer) lasts until the half ends.
public sealed class GrudgeGraph
{
    private readonly Dictionary<(ulong Killer, ulong Victim), GrudgeEdge> _edges = new();
    private readonly HashSet<(ulong Tormentor, ulong Victim)> _redHot = new();

    public void ResetMap()
    {
        _edges.Clear();
        _redHot.Clear();
    }

    public void ResetHalf() => _redHot.Clear();

    public GrudgeEdge RecordKill(ulong killer, ulong victim)
    {
        if (!_edges.TryGetValue((killer, victim), out var edge))
        {
            edge = new GrudgeEdge();
            _edges[(killer, victim)] = edge;
        }

        edge.Kills++;
        edge.Heat += 1;
        if (edge.Heat >= 3)
            _redHot.Add((killer, victim));
        return edge;
    }

    public void DecayRound()
    {
        foreach (var edge in _edges.Values)
            edge.Heat = Math.Max(0, edge.Heat - 0.15);
    }

    public int KillsBetween(ulong killer, ulong victim) =>
        _edges.TryGetValue((killer, victim), out var e) ? e.Kills : 0;

    // Victim of `tormentor` is red-hot at them.
    public bool IsRedHot(ulong victim, ulong tormentor) => _redHot.Contains((tormentor, victim));

    // Revenge: a red-hot victim finally kills their tormentor.
    public bool IsRevenge(ulong killer, ulong victim) => IsRedHot(killer, victim);

    public void ClearRedHot(ulong victim, ulong tormentor) => _redHot.Remove((tormentor, victim));
}

public sealed class KillContext
{
    public string Weapon = string.Empty;
    public bool Headshot;
    public bool Noscope;
    public bool ThruSmoke;
    public bool AttackerBlind;
    public int Penetrated;
    public float Distance;
    public bool AttackerInAir;
    public int AttackerHp;         // attacker HP at the moment of the kill
    public int KillStreakInWindow; // kills within the 5s window, this one included
}

public static class HighlightScorer
{
    private static readonly HashSet<string> Pistols = new(StringComparer.OrdinalIgnoreCase)
    {
        "glock", "usp_silencer", "hkp2000", "p250", "elite", "fiveseven", "tec9", "cz75a", "deagle", "revolver"
    };

    private static readonly HashSet<string> Snipers = new(StringComparer.OrdinalIgnoreCase)
    {
        "awp", "ssg08", "scar20", "g3sg1"
    };

    public static bool IsKnife(string weapon) =>
        weapon.Contains("knife", StringComparison.OrdinalIgnoreCase)
        || weapon.Equals("bayonet", StringComparison.OrdinalIgnoreCase);

    public static int Score(KillContext k)
    {
        int score = 10;

        if (k.Headshot) score += 5;
        if (k.AttackerBlind) score += 25;
        if (k.ThruSmoke) score += 20;
        if (k.Penetrated > 0) score += 15 * k.Penetrated;
        if (k.AttackerInAir) score += 25;
        if (k.Noscope && Snipers.Contains(k.Weapon)) score += 30;
        if (IsKnife(k.Weapon)) score += 40;
        if (k.Weapon.Equals("taser", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (k.AttackerHp is > 0 and <= 20) score += 20;
        if (Pistols.Contains(k.Weapon)) score += 20;
        if (Snipers.Contains(k.Weapon) && k.Distance > 40f) score += 10;

        score += k.KillStreakInWindow switch
        {
            2 => 15,
            3 => 30,
            4 => 50,
            >= 5 => 100,
            _ => 0
        };

        return score;
    }
}
