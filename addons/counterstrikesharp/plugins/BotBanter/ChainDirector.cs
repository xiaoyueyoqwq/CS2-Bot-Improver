namespace BotBanter;

public sealed record SaidEntry(string Speaker, int Round, string Scenario, string Text, string ScoreAtTime);

public sealed class BotCandidate
{
    public string Name = string.Empty;
    public Persona Persona;
    public bool Alive;
    public bool KilledByTarget;      // has been killed by the player being taunted
    public bool HasGrudgeEdge;
    public bool OnCooldown;
}

// Chain orchestration: budgets, pair dedupe, SaidLog, and the comedy-rules casting scorer.
public sealed class ChainDirector
{
    private readonly Random _rng = new();
    private readonly List<SaidEntry> _saidLog = new();
    private readonly HashSet<(string, string)> _usedPairs = new();   // (拆台者, 被拆者) once per map

    private int _teardownsUsed;   // 拆台, max 2/map
    private int _cheersUsed;      // 捧哏, max 2/map
    private int _archUsed;        // 考古, max 2/map
    private bool _infightUsed;    // 内讧, max 1/map

    public void ResetMap()
    {
        _saidLog.Clear();
        _usedPairs.Clear();
        _teardownsUsed = 0;
        _cheersUsed = 0;
        _archUsed = 0;
        _infightUsed = false;
    }

    public void LogSaid(string speaker, int round, string scenario, string text, string score) =>
        _saidLog.Add(new SaidEntry(speaker, round, scenario, text, score));

    public IEnumerable<SaidEntry> SaidLog => _saidLog;

    public bool CanTeardown(string breaker, string target)
    {
        return _teardownsUsed < 2 && !_usedPairs.Contains((breaker, target));
    }

    public void UseTeardown(string breaker, string target)
    {
        _teardownsUsed++;
        _usedPairs.Add((breaker, target));
    }

    public bool TryUseCheer()
    {
        if (_cheersUsed >= 2)
        {
            return false;
        }

        _cheersUsed++;
        return true;
    }

    public bool TryUseArchaeology()
    {
        if (_archUsed >= 2)
        {
            return false;
        }

        _archUsed++;
        return true;
    }

    public bool TryUseInfight()
    {
        if (_infightUsed)
        {
            return false;
        }

        _infightUsed = true;
        return true;
    }

    // 候选打分 (chain doc §3). Target: who is being torn down; null for generic chains.
    public BotCandidate? Cast(IEnumerable<BotCandidate> candidates, bool wantTeardown, bool wantCalm, string? teardownTarget)
    {
        BotCandidate? best = null;
        var bestScore = int.MinValue;

        foreach (var c in candidates)
        {
            if (c.OnCooldown)
            {
                continue; // 冷却惩罚 -100: hard mute
            }

            if (teardownTarget != null && (c.Name == teardownTarget || !CanTeardown(c.Name, teardownTarget)))
            {
                continue; // 拆台的永远不是立flag的人自己; pair once per map
            }

            var score = 0;
            if (wantTeardown && c.Persona is Persona.Yinyang or Persona.Lezi) score += 30;
            if (wantCalm && c.Persona is Persona.Dashu or Persona.Lezi) score += 30;
            if (c.KilledByTarget) score += 20;
            if (c.HasGrudgeEdge) score += 20;
            if (c.Alive) score += 10;
            score += _rng.Next(-10, 11);

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }
}
