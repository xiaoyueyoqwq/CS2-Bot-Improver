using CounterStrikeSharp.API.Modules.Utils;

namespace BotBanter;

public sealed class PlayerStats
{
    public string Name = string.Empty;
    public bool IsBot;
    public CsTeam Team;
    public int Kills;
    public int Deaths;
    public int Damage;
    public int MoneySpent;
    public int ChatMessages;
    public int FirstKills;
    public int LuckyKills;       // blind kill / wallbang / <=5HP survivor kill
    public int ClutchWins;       // 1vX (X>=2) won
    public int RoundsSurvived;
    public List<(float X, float Y)> DeathSpots = new();
    public List<(float X, float Y)> KillSpots = new();

    public double Kd => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

public enum SkillTier { God, Normal, Noob }

// Round/match facts: scores, clutches, kill windows, per-player stats, comeback & mercy state.
public sealed class MatchState
{
    public int Round;                         // 1-based, incremented on round start
    public int RoundsPlayed;                  // completed rounds
    public bool HalftimeReached;
    public double RoundStartTime;

    public int ScoreT;
    public int ScoreCt;

    public readonly Dictionary<string, PlayerStats> Stats = new();

    // per-round
    public bool FirstKillDone;
    public readonly Dictionary<string, List<double>> KillWindow = new();   // name -> kill timestamps
    public readonly Dictionary<string, int> RoundKills = new();
    public string? ClutchCandidate;
    public int ClutchSize;
    public readonly HashSet<string> DeadThisRound = new();
    public int BotFeedsThisRound;                                          // bot teammates dead <10s
    public readonly List<string> RoundKillWeaponsT = new();
    public readonly List<string> RoundKillWeaponsCt = new();
    public double? BombPlantTime;

    // streaks / comeback
    private int _maxDeficitT;
    private int _maxDeficitCt;
    public int HumanTeamLossStreak;
    public CsTeam HumanTeam = CsTeam.None;

    public void ResetMap()
    {
        Round = 0;
        RoundsPlayed = 0;
        HalftimeReached = false;
        ScoreT = 0;
        ScoreCt = 0;
        Stats.Clear();
        _maxDeficitT = 0;
        _maxDeficitCt = 0;
        HumanTeamLossStreak = 0;
        HumanTeam = CsTeam.None;
        ResetRound(0);
    }

    public void ResetRound(double now)
    {
        Round++;
        RoundStartTime = now;
        FirstKillDone = false;
        KillWindow.Clear();
        RoundKills.Clear();
        ClutchCandidate = null;
        ClutchSize = 0;
        DeadThisRound.Clear();
        BotFeedsThisRound = 0;
        RoundKillWeaponsT.Clear();
        RoundKillWeaponsCt.Clear();
        BombPlantTime = null;
    }

    public PlayerStats GetStats(string name, bool isBot, CsTeam team)
    {
        if (!Stats.TryGetValue(name, out var s))
        {
            s = new PlayerStats { Name = name, IsBot = isBot };
            Stats[name] = s;
        }

        s.Team = team;
        s.IsBot = isBot;
        return s;
    }

    public SkillTier TierOf(string name)
    {
        if (!Stats.TryGetValue(name, out var s) || s.Kills + s.Deaths < 3)
        {
            return SkillTier.Normal;
        }

        if (s.Kd > 2) return SkillTier.God;
        if (s.Kd < 0.5) return SkillTier.Noob;
        return SkillTier.Normal;
    }

    public int RegisterKillInWindow(string attacker, double now)
    {
        if (!KillWindow.TryGetValue(attacker, out var times))
        {
            times = new List<double>();
            KillWindow[attacker] = times;
        }

        times.Add(now);
        times.RemoveAll(t => now - t > 5.0);
        RoundKills[attacker] = RoundKills.GetValueOrDefault(attacker) + 1;
        return times.Count;
    }

    public void RecordRoundEnd(CsTeam winner, IEnumerable<PlayerStats> survivors)
    {
        RoundsPlayed++;
        if (winner == CsTeam.Terrorist) ScoreT++;
        else if (winner == CsTeam.CounterTerrorist) ScoreCt++;

        _maxDeficitT = Math.Max(_maxDeficitT, ScoreCt - ScoreT);
        _maxDeficitCt = Math.Max(_maxDeficitCt, ScoreT - ScoreCt);

        foreach (var s in survivors)
        {
            s.RoundsSurvived++;
        }

        if (HumanTeam != CsTeam.None)
        {
            HumanTeamLossStreak = winner == HumanTeam ? 0 : HumanTeamLossStreak + 1;
        }
    }

    // Comeback: was down >=4 at some point and has now tied or taken the lead.
    public bool IsComeback(CsTeam winner)
    {
        return winner switch
        {
            CsTeam.Terrorist => _maxDeficitT >= 4 && ScoreT >= ScoreCt,
            CsTeam.CounterTerrorist => _maxDeficitCt >= 4 && ScoreCt >= ScoreT,
            _ => false
        };
    }

    public int ScoreOf(CsTeam team) => team == CsTeam.Terrorist ? ScoreT : ScoreCt;

    public bool IsMatchPoint(int maxRounds)
    {
        var needed = maxRounds / 2 + 1;
        return ScoreT == needed - 1 || ScoreCt == needed - 1;
    }

    // 军规9: mercy — humans lost 6 straight and their whole team K/D < 0.5.
    public bool MercyActive()
    {
        if (HumanTeam == CsTeam.None || HumanTeamLossStreak < 6)
        {
            return false;
        }

        var humans = Stats.Values.Where(s => !s.IsBot && s.Team == HumanTeam).ToList();
        if (humans.Count == 0)
        {
            return false;
        }

        var kills = humans.Sum(h => h.Kills);
        var deaths = Math.Max(1, humans.Sum(h => h.Deaths));
        return (double)kills / deaths < 0.5;
    }
}
