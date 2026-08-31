namespace BotBanter;

public sealed class GrudgeEdge
{
    public int Kills;       // killer -> victim kills
    public int LastRound;
    public double Heat;
}

// (killer, victim) -> edge. Map-scoped memory behind 红温/宿敌/复仇.
public sealed class GrudgeGraph
{
    private readonly Dictionary<(string, string), GrudgeEdge> _edges = new();
    private readonly HashSet<string> _redHot = new();

    public void ResetMap()
    {
        _edges.Clear();
        _redHot.Clear();
    }

    public void ResetHalf() => _redHot.Clear(); // red-hot lasts until end of half

    public GrudgeEdge RecordKill(string killer, string victim, int round)
    {
        if (!_edges.TryGetValue((killer, victim), out var edge))
        {
            edge = new GrudgeEdge();
            _edges[(killer, victim)] = edge;
        }

        edge.Kills++;
        edge.Heat += 1;
        edge.LastRound = round;

        if (edge.Heat >= 3)
        {
            _redHot.Add(VictimKey(killer, victim));
        }

        return edge;
    }

    public void DecayRound()
    {
        foreach (var edge in _edges.Values)
        {
            edge.Heat = Math.Max(0, edge.Heat - 0.15);
        }
    }

    public int KillsBetween(string killer, string victim) =>
        _edges.TryGetValue((killer, victim), out var e) ? e.Kills : 0;

    public int FirstGrudgeRound(string killer, string victim) =>
        _edges.TryGetValue((killer, victim), out var e) ? e.LastRound - e.Kills + 1 : 0;

    // Victim of `tormentor` is red-hot at them.
    public bool IsRedHot(string victim, string tormentor) => _redHot.Contains(VictimKey(tormentor, victim));

    public bool IsRedHotAtAnyone(string victim) => _redHot.Any(k => k.EndsWith("->" + victim, StringComparison.Ordinal));

    public bool IsNemesisPair(string a, string b) =>
        _edges.TryGetValue((a, b), out var ab) && ab.Heat >= 2
        && _edges.TryGetValue((b, a), out var ba) && ba.Heat >= 2;

    // Revenge: red-hot victim finally kills the tormentor.
    public bool IsRevenge(string killer, string victim) => IsRedHot(killer, victim);

    public void ClearRedHot(string victim, string tormentor) => _redHot.Remove(VictimKey(tormentor, victim));

    // Prefer a bot that has been killed by `player` when choosing who taunts them.
    public string? MostAggrievedBot(string player, IEnumerable<string> candidateBots)
    {
        string? best = null;
        var bestKills = 0;
        foreach (var bot in candidateBots)
        {
            var kills = KillsBetween(player, bot);
            if (kills > bestKills)
            {
                bestKills = kills;
                best = bot;
            }
        }

        return best;
    }

    private static string VictimKey(string tormentor, string victim) => tormentor + "->" + victim;
}
