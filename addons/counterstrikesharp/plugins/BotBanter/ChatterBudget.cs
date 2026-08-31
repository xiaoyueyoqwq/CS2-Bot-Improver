namespace BotBanter;

public enum Freq { Low, Normal, High }

// 军规1-4: budget, cooldowns, silence and delay. The whole "not creepy" layer.
public sealed class ChatterBudget
{
    private readonly Random _rng = new();
    private readonly Dictionary<string, double> _botCooldownUntil = new();
    private readonly Dictionary<string, int> _exemptUsesByCategory = new();
    private readonly Dictionary<string, int> _lastQuestionMarkRound = new();

    public Freq Frequency = Freq.Normal;

    private int _spokenThisMap;
    private bool _spokeThisRound;

    private int MapSoftCap => Frequency switch { Freq.Low => 7, Freq.High => 18, _ => 12 };
    private double FreqSilenceShift => Frequency switch { Freq.Low => 0.15, Freq.High => -0.10, _ => 0 };

    public void ResetMap()
    {
        _botCooldownUntil.Clear();
        _exemptUsesByCategory.Clear();
        _lastQuestionMarkRound.Clear();
        _spokenThisMap = 0;
        _spokeThisRound = false;
    }

    public void ResetRound() => _spokeThisRound = false;

    public int Threshold => 60 + _spokenThisMap * 8;

    // Exempt highlights (ACE/1v5/knife/revenge/comeback) bypass budget, max 2 per category per map.
    public bool TryUseExempt(string category)
    {
        var used = _exemptUsesByCategory.GetValueOrDefault(category);
        if (used >= 2)
        {
            return false;
        }

        _exemptUsesByCategory[category] = used + 1;
        return true;
    }

    public bool OnCooldown(string botName, double now) =>
        _botCooldownUntil.TryGetValue(botName, out var until) && now < until;

    public bool CanSpeak(string botName, Persona persona, int score, bool exempt, bool redHot, double now)
    {
        if (OnCooldown(botName, now))
        {
            return false;
        }

        if (exempt)
        {
            return true;
        }

        if (_spokeThisRound || _spokenThisMap >= MapSoftCap || score < Threshold)
        {
            return false;
        }

        // 军规3: random silence 25~40% (persona-shaped); red-hot bots never shut up.
        if (!redHot)
        {
            var silence = Math.Clamp(PersonaTraits.SilenceRate(persona) + FreqSilenceShift, 0.05, 0.85);
            if (_rng.NextDouble() < silence)
            {
                return false;
            }
        }

        return true;
    }

    public void RecordSpoken(string botName, double now, bool countsAgainstBudget = true)
    {
        _botCooldownUntil[botName] = now + 90 + _rng.NextDouble() * 90;
        if (countsAgainstBudget)
        {
            _spokeThisRound = true;
            _spokenThisMap++;
        }
    }

    // "？" cheap channel: free, but per bot at most once every 3 rounds.
    public bool CanQuestionMark(string botName, int round)
    {
        if (_lastQuestionMarkRound.TryGetValue(botName, out var last) && round - last < 3)
        {
            return false;
        }

        _lastQuestionMarkRound[botName] = round;
        return true;
    }

    // 军规1: 1.5~5s + ~150ms per char typing time.
    public double Delay(string text) => 1.5 + _rng.NextDouble() * 3.5 + text.Length * 0.15;

    public double Roll() => _rng.NextDouble();
    public int RollInt(int maxExclusive) => _rng.Next(maxExclusive);
}
