using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBanter;

[MinimumApiVersion(304)]
public sealed class BotBanterPlugin : BasePlugin
{
    public override string ModuleName => "BotBanter";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "Devin";
    public override string ModuleDescription => "Bots talk trash like real people: highlight-aware, persona-driven, grudge-keeping banter.";

    private bool _enabled = true;
    private int _mapEpoch;

    private Lexicon _lexicon = new();
    private PersonaAssigner _personas = null!;
    private readonly ChatterBudget _budget = new();
    private readonly MatchState _state = new();
    private readonly GrudgeGraph _grudges = new();
    private readonly FlagLedger _flags = new();
    private readonly ChainDirector _chains = new();
    private readonly Random _rng = new();
    private readonly Dictionary<string, int> _feedsByBot = new();
    private bool _openingDone;

    private static readonly Dictionary<string, int> WeaponPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ak47"] = 2700, ["m4a1"] = 2900, ["m4a1_silencer"] = 2900, ["awp"] = 4750, ["ssg08"] = 1700,
        ["famas"] = 1950, ["galilar"] = 1800, ["aug"] = 3300, ["sg556"] = 3000, ["scar20"] = 5000,
        ["g3sg1"] = 5000, ["mp9"] = 1250, ["mac10"] = 1050, ["mp7"] = 1500, ["mp5sd"] = 1500,
        ["ump45"] = 1200, ["p90"] = 2350, ["bizon"] = 1400, ["nova"] = 1050, ["xm1014"] = 2000,
        ["mag7"] = 1300, ["sawedoff"] = 1100, ["m249"] = 5200, ["negev"] = 1700, ["deagle"] = 700,
        ["revolver"] = 600, ["elite"] = 300, ["fiveseven"] = 500, ["tec9"] = 500, ["cz75a"] = 500,
        ["p250"] = 300, ["usp_silencer"] = 200, ["hkp2000"] = 200, ["glock"] = 200, ["taser"] = 200,
        ["hegrenade"] = 300, ["flashbang"] = 200, ["smokegrenade"] = 300, ["molotov"] = 400,
        ["incgrenade"] = 600, ["decoy"] = 50, ["vest"] = 650, ["vesthelm"] = 1000, ["defuser"] = 400
    };

    public override void Load(bool hotReload)
    {
        _personas = new PersonaAssigner(ModuleDirectory);
        try
        {
            _lexicon.Load(ModuleDirectory);
            Server.PrintToConsole($"[BotBanter] Loaded — {_lexicon.TotalLines} lines in lexicon.");
        }
        catch (Exception e)
        {
            Server.PrintToConsole($"[BotBanter] FAILED to load lexicon: {e.Message}");
        }

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        AddCommandListener("say", OnPlayerSay, HookMode.Post);
        AddCommandListener("say_team", OnPlayerSay, HookMode.Post);
        RegisterEventHandler<EventItemPurchase>(OnItemPurchase);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);

        RegisterListener<Listeners.OnMapStart>(_ => ResetMap());

        AddCommand("css_banter", "Toggle bot banter: css_banter <on|off>", OnBanterCommand);
        AddCommand("css_banter_freq", "Set banter frequency: css_banter_freq <low|normal|high>", OnFreqCommand);

        ResetMap();
    }

    private void ResetMap()
    {
        _mapEpoch++;
        _state.ResetMap();
        _budget.ResetMap();
        _grudges.ResetMap();
        _flags.ResetMap();
        _chains.ResetMap();
        _lexicon.ResetMap();
        _personas.Reset();
        _feedsByBot.Clear();
        _openingDone = false;
    }

    // ────────────────────────── commands ──────────────────────────

    private void OnBanterCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!HasCommandAccess(caller))
        {
            command.ReplyToCommand("[BotBanter] no permission.");
            return;
        }

        if (command.ArgCount > 1)
        {
            var arg = command.GetArg(1);
            if (arg.Equals("on", StringComparison.OrdinalIgnoreCase)) _enabled = true;
            else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) _enabled = false;
            else
            {
                command.ReplyToCommand("[BotBanter] usage: css_banter <on|off>");
                return;
            }
        }

        command.ReplyToCommand($"[BotBanter] banter = {(_enabled ? "on" : "off")}");
    }

    private void OnFreqCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!HasCommandAccess(caller))
        {
            command.ReplyToCommand("[BotBanter] no permission.");
            return;
        }

        if (command.ArgCount > 1)
        {
            var arg = command.GetArg(1);
            if (arg.Equals("low", StringComparison.OrdinalIgnoreCase)) _budget.Frequency = Freq.Low;
            else if (arg.Equals("normal", StringComparison.OrdinalIgnoreCase)) _budget.Frequency = Freq.Normal;
            else if (arg.Equals("high", StringComparison.OrdinalIgnoreCase)) _budget.Frequency = Freq.High;
            else
            {
                command.ReplyToCommand("[BotBanter] usage: css_banter_freq <low|normal|high>");
                return;
            }
        }

        command.ReplyToCommand($"[BotBanter] freq = {_budget.Frequency.ToString().ToLowerInvariant()}");
    }

    // ────────────────────────── event handlers ──────────────────────────

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _state.ResetRound(Now());
        _budget.ResetRound();
        _grudges.DecayRound();
        DetectHumanTeam();

        if (!BanterAllowed)
        {
            return HookResult.Continue;
        }

        // 开局寒暄: first round only, one random bot.
        if (!_openingDone && _state.Round <= 1)
        {
            _openingDone = true;
            var bot = RandomBot();
            if (bot != null && _budget.TryUseExempt("opening"))
            {
                SpeakScenario(bot, "opening", BaseVars(), countsBudget: false);
            }
        }

        // 赛点/决胜局施压.
        var maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        if (_state.RoundsPlayed > 0 && _state.IsMatchPoint(maxRounds))
        {
            TrySpeakBudgeted(RandomBot(), "match_point", BaseVars(), score: 70);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker == null || victim == null || !IsTrackable(attacker) || !IsTrackable(victim)
            || attacker.Team == victim.Team)
        {
            return HookResult.Continue;
        }

        var stats = _state.GetStats(attacker.PlayerName, attacker.IsBot, attacker.Team);
        stats.Damage += Math.Max(0, @event.DmgHealth);
        return HookResult.Continue;
    }

    private HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsTrackable(player))
        {
            return HookResult.Continue;
        }

        var weapon = @event.Weapon.Replace("weapon_", string.Empty).Replace("item_", string.Empty);
        if (WeaponPrices.TryGetValue(weapon, out var price))
        {
            _state.GetStats(player.PlayerName, player.IsBot, player.Team).MoneySpent += price;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (victim == null || !IsTrackable(victim))
        {
            return HookResult.Continue;
        }

        var now = Now();
        var victimStats = _state.GetStats(victim.PlayerName, victim.IsBot, victim.Team);
        victimStats.Deaths++;
        _state.DeadThisRound.Add(victim.PlayerName);
        if (victim.PlayerPawn.Value?.AbsOrigin is { } vPos)
        {
            victimStats.DeathSpots.Add((vPos.X, vPos.Y));
        }

        // 队友白给: bot dies within 10s of round start.
        var earlyFeed = victim.IsBot && now - _state.RoundStartTime < 10.0;
        if (earlyFeed)
        {
            _state.BotFeedsThisRound++;
            _feedsByBot[victim.PlayerName] = _feedsByBot.GetValueOrDefault(victim.PlayerName) + 1;
        }

        if (attacker == null || !IsTrackable(attacker) || attacker == victim || attacker.Team == victim.Team)
        {
            if (earlyFeed)
            {
                HandleTeammateFeed(victim);
            }

            UpdateClutchState();
            return HookResult.Continue;
        }

        var attackerStats = _state.GetStats(attacker.PlayerName, attacker.IsBot, attacker.Team);
        attackerStats.Kills++;
        if (attacker.PlayerPawn.Value?.AbsOrigin is { } aPos)
        {
            attackerStats.KillSpots.Add((aPos.X, aPos.Y));
        }

        if (!_state.FirstKillDone)
        {
            _state.FirstKillDone = true;
            attackerStats.FirstKills++;
        }

        var attackerHp = attacker.PlayerPawn.Value?.Health ?? 100;
        if (@event.Attackerblind || @event.Penetrated > 0 || attackerHp is > 0 and <= 5)
        {
            attackerStats.LuckyKills++;
        }

        var kill = new KillContext
        {
            AttackerName = attacker.PlayerName,
            VictimName = victim.PlayerName,
            AttackerIsBot = attacker.IsBot,
            VictimIsBot = victim.IsBot,
            Weapon = @event.Weapon,
            Headshot = @event.Headshot,
            Noscope = @event.Noscope,
            ThruSmoke = @event.Thrusmoke,
            AttackerBlind = @event.Attackerblind,
            Penetrated = @event.Penetrated,
            Distance = @event.Distance,
            AttackerInAir = @event.Attackerinair,
            AttackerHp = attackerHp,
            KillStreakInWindow = _state.RegisterKillInWindow(attacker.PlayerName, now),
            RoundKills = _state.RoundKills.GetValueOrDefault(attacker.PlayerName),
            ClutchSize = _state.ClutchCandidate == attacker.PlayerName ? _state.ClutchSize : 0
        };
        (victim.Team == CsTeam.Terrorist ? _state.RoundKillWeaponsCt : _state.RoundKillWeaponsT).Add(@event.Weapon);

        var grudge = _grudges.RecordKill(attacker.PlayerName, victim.PlayerName, _state.Round);

        if (BanterAllowed)
        {
            ReactToKill(attacker, victim, kill, grudge, now);
        }

        if (earlyFeed)
        {
            HandleTeammateFeed(victim);
        }

        UpdateClutchState();
        return HookResult.Continue;
    }

    private void ReactToKill(CCSPlayerController attacker, CCSPlayerController victim, KillContext kill, GrudgeEdge grudge, double now)
    {
        var score = HighlightScorer.Score(kill);
        var mercy = _state.MercyActive();
        var vars = KillVars(kill);

        // 复仇成功: red-hot bot finally kills its tormentor — 名场面豁免.
        if (kill.AttackerIsBot && !kill.VictimIsBot && _grudges.IsRevenge(kill.AttackerName, kill.VictimName))
        {
            var firstRound = _grudges.FirstGrudgeRound(kill.VictimName, kill.AttackerName);
            _grudges.ClearRedHot(kill.AttackerName, kill.VictimName);
            if (_budget.TryUseExempt("revenge"))
            {
                vars["r"] = Math.Max(1, firstRound).ToString();
                SpeakScenario(attacker, "revenge", vars, countsBudget: false);
                MaybeArchaeology(attacker, "arch_revenge", vars, roundDiff: _state.Round - firstRound);
                MaybeCheer(attacker.PlayerName, attacker.Team);
            }

            return;
        }

        // 玩家 ACE.
        if (!kill.AttackerIsBot && kill.RoundKills >= 5 && _budget.TryUseExempt("ace"))
        {
            SpeakFromEnemyBots(attacker, "ace_clutch", vars);
            return;
        }

        // 玩家刀杀 BOT — 名场面豁免.
        if (!kill.AttackerIsBot && kill.VictimIsBot && HighlightScorer.IsKnife(kill.Weapon) && _budget.TryUseExempt("knife"))
        {
            SpeakVictimOrTeammate(victim, "god_play", vars);
            return;
        }

        // 玩家说过"我卡了"又被 BOT 杀 → 拆台.
        if (kill.AttackerIsBot && !kill.VictimIsBot && !mercy)
        {
            var lagFlag = _flags.FindBustable(_state.Round, f =>
                !f.SpeakerIsBot && f.Claim == FlagClaim.LagExcuse && f.Speaker == kill.VictimName);
            if (lagFlag != null && _chains.CanTeardown(kill.AttackerName, kill.VictimName))
            {
                lagFlag.Cashed = true;
                _chains.UseTeardown(kill.AttackerName, kill.VictimName);
                SpeakScenario(attacker, "lag_excuse_bust", vars, countsBudget: false, minDelay: 2.0, maxDelay: 6.0);
                return;
            }
        }

        // 红温: same player killed same bot 3+ times. Red-hot bot always talks (沉默率0).
        if (kill.VictimIsBot && !kill.AttackerIsBot && _grudges.IsRedHot(kill.VictimName, kill.AttackerName)
            && PersonaTraits.SpeaksWhenKilled(_personas.Get(kill.VictimName)))
        {
            // A busted KillVow ("这把不弄死你我不玩了" then died again) gets torn down by a teammate.
            var vow = _flags.FindBustable(_state.Round, f =>
                f.SpeakerIsBot && f.Claim == FlagClaim.KillVow && f.Speaker == kill.VictimName && f.Target == kill.AttackerName);
            if (vow != null)
            {
                BustBotFlag(vow, victim.Team);
            }
            else if (TrySpeakBudgeted(victim, "redhot", RedHotVars(kill, grudge), score: Math.Max(score, 60), redHot: true))
            {
                return;
            }

            return;
        }

        if (kill.VictimIsBot && !kill.AttackerIsBot)
        {
            // 被神仙操作单杀.
            if (score >= 60)
            {
                if (TrySpeakBudgeted(victim, "god_play", vars, score))
                {
                    return;
                }
            }
            // 被混子干掉.
            else if (!mercy && _state.TierOf(kill.AttackerName) == SkillTier.Noob)
            {
                if (TrySpeakBudgeted(victim, "killed_by_noob", vars, score: Math.Max(score, 60)))
                {
                    return;
                }
            }

            // "？"低成本通道: decent-but-not-crazy kills, free of budget.
            if (score is >= 30 and < 60 && _budget.Roll() < 0.30
                && PersonaTraits.SpeaksWhenKilled(_personas.Get(kill.VictimName))
                && _budget.CanQuestionMark(kill.VictimName, _state.Round))
            {
                Emit(kill.VictimName, "？", "qmark", countsBudget: false);
            }

            return;
        }

        // BOT 轻松虐菜.
        if (kill.AttackerIsBot && !kill.VictimIsBot && !mercy
            && _state.TierOf(kill.VictimName) == SkillTier.Noob
            && (_state.Stats[kill.VictimName].Deaths >= 3 || HighlightScorer.IsKnife(kill.Weapon) || kill.AttackerBlind))
        {
            TrySpeakBudgeted(attacker, "easy_kill", NoobVars(kill), score: Math.Max(score, 60));
        }
    }

    private void HandleTeammateFeed(CCSPlayerController feeder)
    {
        if (!BanterAllowed)
        {
            return;
        }

        var teammate = RandomBot(feeder.Team, exclude: feeder.PlayerName);
        if (teammate == null)
        {
            return;
        }

        var vars = BaseVars();
        vars["vic"] = feeder.PlayerName;
        TrySpeakBudgeted(teammate, "teammate_feed", vars, score: 62);
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        _state.BombPlantTime = Now();
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (!BanterAllowed || @event.Userid is not { } defuser || !IsTrackable(defuser))
        {
            return HookResult.Continue;
        }

        var tAlive = AlivePlayers(CsTeam.Terrorist).Count;
        var remaining = _state.BombPlantTime is { } planted ? 40.0 - (Now() - planted) : 40.0;
        var isNinja = tAlive >= 1;
        var isLastSecond = remaining < 1.5;
        if (!isNinja && !isLastSecond)
        {
            return HookResult.Continue;
        }

        if (!_budget.TryUseExempt("ninja"))
        {
            return HookResult.Continue;
        }

        var vars = BaseVars();
        if (defuser.IsBot)
        {
            SpeakScenario(defuser, "ninja_defuse", vars, countsBudget: false);
        }
        else
        {
            SpeakFromEnemyBots(defuser, "ninja_defuse", vars);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !IsTrackable(player) || player.IsBot)
        {
            return HookResult.Continue;
        }

        _state.GetStats(player.PlayerName, false, player.Team).ChatMessages++;

        var text = command.ArgCount > 1 ? command.ArgString.Trim('"') : string.Empty;
        if (FlagLedger.LooksLikeLagExcuse(text))
        {
            _flags.Register(player.PlayerName, false, _state.Round, FlagClaim.LagExcuse, text);
        }
        else if (FlagLedger.LooksLikePlayerFlag(text))
        {
            _flags.Register(player.PlayerName, false, _state.Round, FlagClaim.ComebackBoast, text);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        var winner = (CsTeam)@event.Winner;
        if (winner is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            return HookResult.Continue;
        }

        // Clutch bookkeeping before survivors reset.
        if (_state.ClutchCandidate is { } clutcher && _state.ClutchSize >= 2
            && _state.Stats.TryGetValue(clutcher, out var clutchStats)
            && clutchStats.Team == winner && !_state.DeadThisRound.Contains(clutcher))
        {
            clutchStats.ClutchWins++;
            if (!clutchStats.IsBot && _state.ClutchSize >= 3 && BanterAllowed
                && _budget.TryUseExempt("clutch"))
            {
                var clutchVars = BaseVars();
                clutchVars["atk"] = clutcher;
                clutchVars["n"] = _state.ClutchSize.ToString();
                var enemyBot = RandomBot(Opposite(winner));
                if (enemyBot != null)
                {
                    SpeakScenario(enemyBot, "ace_clutch", clutchVars, countsBudget: false);
                }
            }
        }

        var aliveNames = AlivePlayers(CsTeam.Terrorist).Concat(AlivePlayers(CsTeam.CounterTerrorist))
            .Select(p => p.PlayerName)
            .ToHashSet();
        var survivors = _state.Stats.Values.Where(s => aliveNames.Contains(s.Name));
        _state.RecordRoundEnd(winner, survivors);

        if (!BanterAllowed)
        {
            return HookResult.Continue;
        }

        var loser = Opposite(winner);
        var mercy = _state.MercyActive();

        // 大翻盘 — 名场面豁免.
        if (_state.IsComeback(winner) && _budget.TryUseExempt("comeback"))
        {
            var bot = RandomBot(winner);
            if (bot != null)
            {
                SpeakScenario(bot, "comeback", BaseVars(), countsBudget: false);
                MaybeCheer(bot.PlayerName, winner);
            }
        }

        // Flag 拆台: boast flags of the losing side bust when their team loses.
        var bustable = _flags.FindBustable(_state.Round, f =>
            f.Claim is FlagClaim.ComebackBoast or FlagClaim.TauntEnemy
            && TeamOf(f.Speaker) == loser
            && (!mercy || f.SpeakerIsBot));
        if (bustable != null)
        {
            if (bustable.SpeakerIsBot)
            {
                BustBotFlag(bustable, TeamOf(bustable.Speaker) ?? loser);
            }
            else
            {
                BustPlayerFlag(bustable, winner);
            }
        }

        // eco 奇迹: winners killed with pistols/knife only, losers were kitted.
        var winnerWeapons = winner == CsTeam.Terrorist ? _state.RoundKillWeaponsT : _state.RoundKillWeaponsCt;
        var loserWeapons = winner == CsTeam.Terrorist ? _state.RoundKillWeaponsCt : _state.RoundKillWeaponsT;
        if (winnerWeapons.Count >= 3
            && winnerWeapons.All(w => HighlightScorer.IsPistol(w) || HighlightScorer.IsKnife(w))
            && loserWeapons.Any(w => !HighlightScorer.IsPistol(w) && !HighlightScorer.IsKnife(w))
            && _budget.TryUseExempt("eco"))
        {
            var bot = RandomBot();
            if (bot != null)
            {
                SpeakScenario(bot, "eco_miracle", BaseVars(), countsBudget: false);
            }
        }

        // 内讧: a bot fed >=2 times this map and its team just lost. Fixed 3-beat, once per map.
        TryInfight(loser);

        // 中场换边.
        var maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        if (!_state.HalftimeReached && _state.RoundsPlayed == maxRounds / 2)
        {
            _state.HalftimeReached = true;
            _grudges.ResetHalf();
            var bot = RandomBot();
            if (bot != null && _budget.TryUseExempt("halftime"))
            {
                var myScore = _state.ScoreOf(bot.Team);
                var theirScore = _state.ScoreOf(Opposite(bot.Team));
                var scenario = myScore >= theirScore ? "halftime_lead" : "halftime_behind";
                SpeakScenario(bot, scenario, BaseVars(), countsBudget: false);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (!BanterAllowed)
        {
            return HookResult.Continue;
        }

        // 考古收尾: "就打一把" flag at the last round.
        var lastGame = _flags.FindBustable(_state.Round, f => f.Claim == FlagClaim.LastGame);
        if (lastGame != null && _chains.TryUseArchaeology())
        {
            lastGame.Cashed = true;
            var vars = BaseVars();
            vars["round"] = _state.RoundsPlayed.ToString();
            var pick = _lexicon.Pick("arch_lastgame", _personas.Get(lastGame.Speaker), vars);
            if (pick != null)
            {
                Emit(lastGame.Speaker, pick.Text, "arch_lastgame", countsBudget: false);
            }
        }

        // 颁奖 after a beat so it lands on the win panel.
        var epoch = _mapEpoch;
        AddTimer(3.0f, () =>
        {
            if (epoch != _mapEpoch || !_enabled)
            {
                return;
            }

            var humans = Utilities.GetPlayers().Where(p => IsTrackable(p) && !p.IsBot).ToList();
            AwardsCeremony.Run(_state, _lexicon, humans, _rng);
        });
        return HookResult.Continue;
    }

    // ────────────────────────── chains ──────────────────────────

    private void BustPlayerFlag(Flag flag, CsTeam teardownTeam)
    {
        var breaker = _chains.Cast(Candidates(teardownTeam), wantTeardown: true, wantCalm: false, flag.Speaker);
        if (breaker == null)
        {
            return;
        }

        flag.Cashed = true;
        _chains.UseTeardown(breaker.Name, flag.Speaker);
        var vars = BaseVars();
        vars["vic"] = flag.Speaker;
        var pick = _lexicon.Pick("player_flag_bust", breaker.Persona, vars);
        if (pick != null)
        {
            Emit(breaker.Name, pick.Text, "player_flag_bust", countsBudget: false, minDelay: 2.0, maxDelay: 6.0);
            MaybeCheer(breaker.Name, teardownTeam);
        }
    }

    private void BustBotFlag(Flag flag, CsTeam team)
    {
        var breaker = _chains.Cast(
            Candidates(team).Where(c => c.Name != flag.Speaker), wantTeardown: true, wantCalm: false, flag.Speaker);
        if (breaker == null)
        {
            return;
        }

        flag.Cashed = true;
        _chains.UseTeardown(breaker.Name, flag.Speaker);
        var vars = BaseVars();
        vars["vic"] = flag.Speaker;
        var pick = _lexicon.Pick("bot_flag_bust", breaker.Persona, vars);
        if (pick != null)
        {
            Emit(breaker.Name, pick.Text, "bot_flag_bust", countsBudget: false, minDelay: 2.0, maxDelay: 6.0);
        }
    }

    // 捧哏: 15% short follow-up 3~8s later, never 高冷, free, max 2/map.
    private void MaybeCheer(string speaker, CsTeam team)
    {
        if (_budget.Roll() > 0.15)
        {
            return;
        }

        var cheerer = Candidates(team)
            .Where(c => c.Name != speaker && !c.OnCooldown && PersonaTraits.CanCheer(c.Persona))
            .OrderBy(_ => _rng.Next())
            .FirstOrDefault();
        if (cheerer == null || !_chains.TryUseCheer())
        {
            return;
        }

        var pick = _lexicon.PickCheer();
        if (pick != null)
        {
            Emit(cheerer.Name, pick.Text, "cheer", countsBudget: false, minDelay: 3.0, maxDelay: 8.0);
        }
    }

    // 考古: only when the callback spans >= 4 rounds, max 2/map.
    private void MaybeArchaeology(CCSPlayerController bot, string scenario, Dictionary<string, string> vars, int roundDiff)
    {
        if (roundDiff < 4 || !_chains.TryUseArchaeology())
        {
            return;
        }

        var pick = _lexicon.Pick(scenario, _personas.Get(bot.PlayerName), vars);
        if (pick != null)
        {
            Emit(bot.PlayerName, pick.Text, scenario, countsBudget: false, minDelay: 4.0, maxDelay: 8.0);
        }
    }

    // 内讧固定三段式: 点名 → 回嘴 → 灭火. Once per map.
    private void TryInfight(CsTeam losingTeam)
    {
        var feeder = _feedsByBot
            .Where(kv => kv.Value >= 2 && TeamOf(kv.Key) == losingTeam && IsBotName(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (feeder == null)
        {
            return;
        }

        var candidates = Candidates(losingTeam).Where(c => c.Name != feeder).ToList();
        var caller = candidates.FirstOrDefault(c => c.Persona == Persona.Baozao && !c.OnCooldown)
                     ?? candidates.FirstOrDefault(c => !c.OnCooldown);
        var calmer = _chains.Cast(candidates.Where(c => c.Name != caller?.Name), wantTeardown: false, wantCalm: true, null);
        if (caller == null || calmer == null || !_chains.TryUseInfight())
        {
            return;
        }

        var feederPersona = _personas.Get(feeder);
        var callerStats = _state.Stats.GetValueOrDefault(caller.Name);

        var callVars = BaseVars();
        callVars["vic"] = feeder;
        callVars["n"] = _feedsByBot[feeder].ToString();
        var call = _lexicon.Pick("infight_call", caller.Persona, callVars);
        if (call == null)
        {
            return;
        }

        var replyVars = BaseVars();
        replyVars["atk"] = caller.Name;
        replyVars["kd"] = callerStats != null ? $"{callerStats.Kills}/{callerStats.Deaths}" : string.Empty;
        var reply = _lexicon.Pick("infight_reply", feederPersona, replyVars);

        var calm = _lexicon.Pick("infight_calm", calmer.Persona, BaseVars());

        Emit(caller.Name, call.Text, "infight_call", countsBudget: false, minDelay: 2.0, maxDelay: 4.0);
        // 高冷回嘴 = 沉默, also part of the bit.
        var epoch = _mapEpoch;
        var replyDelay = 6.0 + _rng.NextDouble() * 3.0;
        if (reply != null && feederPersona != Persona.Gaoleng)
        {
            AddTimer((float)replyDelay, () => PrintLine(feeder, reply.Text, "infight_reply", epoch));
        }

        if (calm != null)
        {
            AddTimer((float)(replyDelay + 4.0 + _rng.NextDouble() * 3.0), () => PrintLine(calmer.Name, calm.Text, "infight_calm", epoch));
        }
    }

    // ────────────────────────── speaking core ──────────────────────────

    private bool TrySpeakBudgeted(CCSPlayerController? bot, string scenario, Dictionary<string, string> vars, int score, bool redHot = false)
    {
        if (bot == null || !bot.IsBot || !BanterAllowed)
        {
            return false;
        }

        var persona = _personas.Get(bot.PlayerName);
        if (!_budget.CanSpeak(bot.PlayerName, persona, score, exempt: false, redHot, Now()))
        {
            return false;
        }

        return SpeakScenario(bot, scenario, vars, countsBudget: true);
    }

    private bool SpeakScenario(CCSPlayerController bot, string scenario, Dictionary<string, string> vars,
        bool countsBudget, double minDelay = 0, double maxDelay = 0)
    {
        var pick = _lexicon.Pick(scenario, _personas.Get(bot.PlayerName), vars);
        if (pick == null)
        {
            return false;
        }

        Emit(bot.PlayerName, pick.Text, scenario, countsBudget, minDelay, maxDelay);

        if (pick.Flag != null)
        {
            var claim = pick.Flag switch
            {
                "kill_vow" => FlagClaim.KillVow,
                "last_game" => FlagClaim.LastGame,
                "taunt" => FlagClaim.TauntEnemy,
                _ => FlagClaim.ComebackBoast
            };
            _flags.Register(bot.PlayerName, true, _state.Round, claim, pick.Text, vars.GetValueOrDefault("atk"));
        }

        return true;
    }

    private void Emit(string botName, string text, string scenario, bool countsBudget,
        double minDelay = 0, double maxDelay = 0)
    {
        var delay = maxDelay > 0
            ? minDelay + _rng.NextDouble() * (maxDelay - minDelay) + text.Length * 0.15
            : _budget.Delay(text);

        _budget.RecordSpoken(botName, Now(), countsBudget);
        var epoch = _mapEpoch;
        AddTimer((float)delay, () => PrintLine(botName, text, scenario, epoch));
    }

    private void PrintLine(string botName, string text, string scenario, int epoch)
    {
        // Delayed sends revalidate: map unchanged, plugin still on, bot still on the server.
        if (epoch != _mapEpoch || !_enabled || FindPlayer(botName) == null)
        {
            return;
        }

        Server.PrintToChatAll($" {botName}: {text}");
        _chains.LogSaid(botName, _state.Round, scenario, text, $"{_state.ScoreT}:{_state.ScoreCt}");
        if (_state.Stats.TryGetValue(botName, out var stats))
        {
            stats.ChatMessages++;
        }
    }

    private void SpeakFromEnemyBots(CCSPlayerController player, string scenario, Dictionary<string, string> vars)
    {
        var enemyTeam = Opposite(player.Team);
        var bots = AliveOrDeadBots(enemyTeam).Select(b => b.PlayerName).ToList();
        var chosen = _grudges.MostAggrievedBot(player.PlayerName, bots) ?? bots.OrderBy(_ => _rng.Next()).FirstOrDefault();
        if (chosen == null)
        {
            return;
        }

        var bot = FindPlayer(chosen);
        if (bot != null)
        {
            SpeakScenario(bot, scenario, vars, countsBudget: false);
            MaybeCheer(chosen, enemyTeam);
        }
    }

    private void SpeakVictimOrTeammate(CCSPlayerController victim, string scenario, Dictionary<string, string> vars)
    {
        if (PersonaTraits.SpeaksWhenKilled(_personas.Get(victim.PlayerName)))
        {
            SpeakScenario(victim, scenario, vars, countsBudget: false);
            return;
        }

        var teammate = RandomBot(victim.Team, exclude: victim.PlayerName);
        if (teammate != null)
        {
            SpeakScenario(teammate, scenario, vars, countsBudget: false);
        }
    }

    // ────────────────────────── vars & helpers ──────────────────────────

    private Dictionary<string, string> BaseVars() => new()
    {
        ["score"] = $"{_state.ScoreT}:{_state.ScoreCt}",
        ["round"] = _state.Round.ToString()
    };

    private Dictionary<string, string> KillVars(KillContext kill)
    {
        var vars = BaseVars();
        vars["atk"] = kill.AttackerName;
        vars["vic"] = kill.VictimName;
        vars["wpn"] = kill.Weapon;
        if (_state.Stats.TryGetValue(kill.AttackerName, out var s))
        {
            vars["kd"] = $"{s.Kills}/{s.Deaths}";
            vars["deaths"] = s.Deaths.ToString();
        }

        return vars;
    }

    private Dictionary<string, string> NoobVars(KillContext kill)
    {
        var vars = BaseVars();
        vars["atk"] = kill.AttackerName;
        vars["vic"] = kill.VictimName;
        vars["wpn"] = kill.Weapon;
        if (_state.Stats.TryGetValue(kill.VictimName, out var s))
        {
            vars["kd"] = $"{s.Kills}/{s.Deaths}";
            vars["deaths"] = s.Deaths.ToString();
        }

        return vars;
    }

    private Dictionary<string, string> RedHotVars(KillContext kill, GrudgeEdge grudge)
    {
        var vars = KillVars(kill);
        vars["n"] = grudge.Kills.ToString();
        return vars;
    }

    private void UpdateClutchState()
    {
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var alive = AlivePlayers(team);
            var enemyAlive = AlivePlayers(Opposite(team)).Count;
            if (alive.Count == 1 && enemyAlive >= 2)
            {
                var name = alive[0].PlayerName;
                if (_state.ClutchCandidate != name)
                {
                    _state.ClutchCandidate = name;
                    _state.ClutchSize = enemyAlive;
                }
                else
                {
                    _state.ClutchSize = Math.Max(_state.ClutchSize, enemyAlive);
                }
            }
        }
    }

    private void DetectHumanTeam()
    {
        var humans = Utilities.GetPlayers().Where(p => IsTrackable(p) && !p.IsBot).ToList();
        _state.HumanTeam = humans
            .GroupBy(p => p.Team)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault(CsTeam.None);
    }

    private IEnumerable<BotCandidate> Candidates(CsTeam team)
    {
        var now = Now();
        return AliveOrDeadBots(team).Select(b => new BotCandidate
        {
            Name = b.PlayerName,
            Persona = _personas.Get(b.PlayerName),
            Alive = b.PawnIsAlive,
            OnCooldown = _budget.OnCooldown(b.PlayerName, now)
        }).ToList();
    }

    private List<CCSPlayerController> AlivePlayers(CsTeam team) =>
        Utilities.GetPlayers().Where(p => IsTrackable(p) && p.Team == team && p.PawnIsAlive).ToList();

    private List<CCSPlayerController> AliveOrDeadBots(CsTeam team) =>
        Utilities.GetPlayers().Where(p => IsTrackable(p) && p.IsBot && p.Team == team).ToList();

    private CCSPlayerController? RandomBot(CsTeam? team = null, string? exclude = null)
    {
        var bots = Utilities.GetPlayers()
            .Where(p => IsTrackable(p) && p.IsBot && (team == null || p.Team == team) && p.PlayerName != exclude)
            .ToList();
        return bots.Count == 0 ? null : bots[_rng.Next(bots.Count)];
    }

    private CCSPlayerController? FindPlayer(string name) =>
        Utilities.GetPlayers().FirstOrDefault(p => IsTrackable(p) && p.PlayerName == name);

    private CsTeam? TeamOf(string name) =>
        _state.Stats.TryGetValue(name, out var s) ? s.Team : FindPlayer(name)?.Team;

    private bool IsBotName(string name) => FindPlayer(name)?.IsBot ?? _state.Stats.GetValueOrDefault(name)?.IsBot ?? false;

    private static CsTeam Opposite(CsTeam team) =>
        team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

    private bool BanterAllowed => _enabled && _lexicon.Loaded && !IsWarmup() && !IsDeathmatch();

    private static bool IsWarmup() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;

    private static bool IsDeathmatch() =>
        (ConVar.Find("game_type")?.GetPrimitiveValue<int>() ?? 0) == 1
        && (ConVar.Find("game_mode")?.GetPrimitiveValue<int>() ?? 0) == 2;

    private static bool HasCommandAccess(CCSPlayerController? caller) =>
        caller == null || AdminManager.PlayerHasPermissions(caller, "@css/generic");

    private static bool IsTrackable(CCSPlayerController? player) =>
        player is { IsValid: true, IsHLTV: false }
        && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist;

    private static double Now() => Server.CurrentTime;
}
