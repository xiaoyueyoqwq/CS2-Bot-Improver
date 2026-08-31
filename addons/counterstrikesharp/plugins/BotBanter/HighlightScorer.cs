namespace BotBanter;

public sealed class KillContext
{
    public string AttackerName = string.Empty;
    public string VictimName = string.Empty;
    public bool AttackerIsBot;
    public bool VictimIsBot;
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
    public int RoundKills;         // attacker kills this round, this one included
    public int ClutchSize;         // X of an ongoing 1vX for the attacker, 0 if none
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

    public static bool IsPistol(string weapon) => Pistols.Contains(weapon);

    public static int Score(KillContext k)
    {
        var score = 10;

        if (k.Headshot) score += 5;
        if (k.AttackerBlind) score += 25;
        if (k.ThruSmoke) score += 20;
        if (k.Penetrated > 0) score += 15 * k.Penetrated;
        if (k.AttackerInAir) score += 25;
        if (k.Noscope && Snipers.Contains(k.Weapon)) score += 30;
        if (IsKnife(k.Weapon)) score += 40;
        if (k.Weapon.Equals("taser", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (k.AttackerHp is > 0 and <= 20) score += 20;
        if (IsPistol(k.Weapon)) score += 20;
        if (k.Weapon.Equals("ssg08", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (Snipers.Contains(k.Weapon) && k.Distance > 40f) score += 10;

        score += k.KillStreakInWindow switch
        {
            2 => 15,
            3 => 30,
            4 => 50,
            >= 5 => 100,
            _ => 0
        };

        score += k.ClutchSize switch
        {
            3 => 40,
            4 => 70,
            >= 5 => 100,
            _ => 0
        };

        return score;
    }
}
