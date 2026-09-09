// BotChat base plugin: greetings, match-end lines, score-gap taunts, and kill reactions.
//
// Convars:
//   botchat_enabled              - master switch (default 1)
//   botchat_start_enabled        - greetings at match start (default 1)
//   botchat_halftime_enabled     - halftime messages (default 1)
//   botchat_end_enabled          - goodbyes and taunts at match end (default 1)
//   botchat_killreactions_enabled - kill reactions (ns / thanks) (default 1)

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Capabilities;
using BotChatApi;

namespace BotChat;

public class BotChatPlugin : BasePlugin, IPluginConfig<BotChatConfig>
{
    public override string ModuleName => "BotChat";
    public override string ModuleVersion => "2.0.1";
    public override string ModuleAuthor => "Fimall";
    public override string ModuleDescription =>
        "Bots greet at match start, say gg at match end, and chat about kills";

    public BotChatConfig Config { get; set; } = new();

    private (string message, int weight)[] _startMessages = [];
    private (string message, int weight)[] _halfTimeMessages = [];
    private (string message, int weight)[] _matchEndMessages = [];
    private (string message, int weight)[] _dominantWinTauntMessages = [];
    private (string message, int weight)[] _halfTimeTauntMessages = [];
    private (string message, int weight)[] _afterWinTauntMessages = [];
    private (string message, int weight)[] _niceShotMessages = [];
    private (string message, int weight)[] _headshotMessages = [];
    private (string message, int weight)[] _smokeMessages = [];
    private (string message, int weight)[] _wallbangMessages = [];
    private (string message, int weight)[] _blindKillMessages = [];
    private (string message, int weight)[] _airborneKillMessages = [];
    private (string message, int weight)[] _thanksMessages = [];

    private const int AbsoluteMaxSpeakersPerTeam = 5;

    // Random gap between two bot messages (seconds). Keeps chat from looking
    // like a scripted burst.
    private const float MinGap = 1.2f;
    private const float MaxGap = 3.0f;

    // Reaction delay after a death (seconds): 1.2 - 3.5s, so replies read
    // like a human thinking, not an instant script.
    private const float MinReactionDelay = 1.2f;
    private const float MaxReactionDelay = 3.5f;

    // Guards the end messages against double-firing: the final round's win
    // panel and the match win panel may both be dispatched by the engine.
    private bool _endSaid;

    // Bots that owe a thanks reply this round (their shot got complimented
    // with "ns"). They thank on their own death, or at round end if alive.
    private readonly HashSet<ulong> _owedThanks = new();
    private readonly BotChatApiImpl _api;
    private bool _startGreetingDone;
    private bool _startGreetingScheduled;

    public BotChatPlugin()
    {
        _api = new BotChatApiImpl(this);
    }

    public FakeConVar<bool> Enabled = new("botchat_enabled", "Enable bot chat messages", true);
    public FakeConVar<bool> StartEnabled = new("botchat_start_enabled", "Bots greet at match start", true);
    public FakeConVar<bool> HalfTimeEnabled = new("botchat_halftime_enabled", "Bots chat at halftime", true);
    public FakeConVar<bool> EndEnabled = new("botchat_end_enabled", "Bots say goodbye at match end", true);
    public FakeConVar<bool> KillReactionsEnabled = new("botchat_killreactions_enabled", "Bots react to kills (ns / thanks)", true);
    public static PluginCapability<IBotChatApi> Capability { get; } = BotChatCapability.Cap;

    public void OnConfigParsed(BotChatConfig config)
    {
        Config = config ?? new BotChatConfig();
        Config.Version = 2;
        Config.Chat ??= new BotChatFrequencyConfig();
        Config.Chat.Normalize();
        Config.Taunts ??= new BotChatTauntConfig();
        Config.Taunts.Normalize();
    }

    public override void Load(bool hotReload)
    {
        LoadMessages();
        Capabilities.RegisterPluginCapability(Capability, () => _api);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetMatchState();
        });
        RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatch);
        RegisterEventHandler<EventTeamIntroStart>(OnTeamIntroStart);
        RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
        RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
        // cs_win_panel_round fires at the end of every round; FinalEvent = 1
        // marks the final round of the match. More reliable than
        // cs_win_panel_match in offline bot matches, where that event may
        // never be dispatched.
        RegisterEventHandler<EventCsWinPanelRound>(OnCsWinPanelRound);
        RegisterEventHandler<EventCsWinPanelMatch>(OnCsWinPanelMatch);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    }

    private HookResult OnBeginNewMatch(EventBeginNewMatch @event, GameEventInfo info)
    {
        ResetMatchState();
        // The opening greeting is scheduled from the first formal round below.
        // BeginNewMatch can occur during warmup, before teams and BOTs exist.
        return HookResult.Continue;
    }

    private void ResetMatchState()
    {
        _endSaid = false;
        _startGreetingDone = false;
        _startGreetingScheduled = false;
        _owedThanks.Clear();
        _api.ResetReservations();
    }

    private HookResult OnTeamIntroStart(EventTeamIntroStart @event, GameEventInfo info)
    {
        Console.WriteLine($"[BotChat] team intro start (warmup={IsWarmupPeriod()})");
        // This is the team presentation shown at the start of a formal match.
        // Warmup can also emit intro events, so keep the explicit warmup gate.
        if (!_startGreetingDone && !_startGreetingScheduled
            && !IsDeathmatch() && Enabled.Value && StartEnabled.Value
            && !IsWarmupPeriod())
        {
            ScheduleStartGreeting();
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        Console.WriteLine($"[BotChat] match start announce (warmup={IsWarmupPeriod()})");
        if (!_startGreetingDone && !_startGreetingScheduled
            && !IsDeathmatch() && Enabled.Value && StartEnabled.Value)
        {
            ScheduleStartGreeting();
        }

        return HookResult.Continue;
    }

    private HookResult OnStartHalftime(EventStartHalftime @event, GameEventInfo info)
    {
        if (IsDeathmatch() || !Enabled.Value || !HalfTimeEnabled.Value)
            return HookResult.Continue;

        if (RollPercent(Config.Chat.HalftimeMessageChancePercent))
            SayAcrossTeams(_halfTimeMessages, uniform: true, baseDelay: 1.0f,
                featureEnabled: () => HalfTimeEnabled.Value);
        AddTimer(0.2f, TrySayHalftimeTaunt);
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelRound(EventCsWinPanelRound @event, GameEventInfo info)
    {
        if (@event.FinalEvent == 0)
            return HookResult.Continue;

        SayEndMessages();
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        SayEndMessages();
        return HookResult.Continue;
    }

    private void SayEndMessages()
    {
        if (_endSaid)
            return;
        if (IsDeathmatch() || !Enabled.Value || !EndEnabled.Value)
            return;

        _endSaid = true;
        if (RollPercent(Config.Chat.MatchEndMessageChancePercent))
            SayAcrossTeams(_matchEndMessages, uniform: false, baseDelay: 1.5f,
                featureEnabled: () => EndEnabled.Value);
        AddTimer(0.2f, TrySayMatchEndTaunt);
    }

    private void TrySayHalftimeTaunt()
    {
        if (IsDeathmatch() || !Enabled.Value || !HalfTimeEnabled.Value || !Config.Taunts.Enabled)
            return;
        if (!TryGetTeamScores(firstHalfOnly: true, out int terroristScore, out int counterTerroristScore))
            return;

        int scoreGap = Math.Abs(terroristScore - counterTerroristScore);
        if (scoreGap < Config.Taunts.HalftimeScoreGapThreshold
            || !RollPercent(Config.Taunts.HalftimeChancePercent))
            return;

        CsTeam leadingTeam = terroristScore > counterTerroristScore
            ? CsTeam.Terrorist
            : CsTeam.CounterTerrorist;
        SayToTeam(leadingTeam, _halfTimeTauntMessages, baseDelay: 2.5f,
            featureEnabled: () => HalfTimeEnabled.Value && Config.Taunts.Enabled);
    }

    private void TrySayMatchEndTaunt()
    {
        if (IsDeathmatch() || !Enabled.Value || !EndEnabled.Value || !Config.Taunts.Enabled)
            return;
        if (!TryGetTeamScores(firstHalfOnly: false, out int terroristScore, out int counterTerroristScore))
            return;
        if (terroristScore == counterTerroristScore)
            return;

        int scoreGap = Math.Abs(terroristScore - counterTerroristScore);
        bool dominantWin = scoreGap >= Config.Taunts.DominantWinScoreGapThreshold;
        int chance = dominantWin
            ? Config.Taunts.DominantWinChancePercent
            : Config.Taunts.AfterWinChancePercent;
        if (!RollPercent(chance))
            return;

        CsTeam winningTeam = terroristScore > counterTerroristScore
            ? CsTeam.Terrorist
            : CsTeam.CounterTerrorist;
        var pool = dominantWin ? _dominantWinTauntMessages : _afterWinTauntMessages;
        SayToTeam(winningTeam, pool, baseDelay: 3.0f,
            featureEnabled: () => EndEnabled.Value && Config.Taunts.Enabled);
    }

    private void SayToTeam(
        CsTeam team,
        (string message, int weight)[] pool,
        float baseDelay,
        Func<bool>? featureEnabled = null)
    {
        if (pool.Length == 0)
            return;
        var bots = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid
                && p.IsBot
                && !p.IsHLTV
                && p.Team == team
                && !p.HasBeenControlledByPlayerThisRound)
            .ToList();
        SayRandom(bots, pool, uniform: true, baseDelay, featureEnabled);
    }

    private static bool TryGetTeamScores(
        bool firstHalfOnly,
        out int terroristScore,
        out int counterTerroristScore)
    {
        terroristScore = 0;
        counterTerroristScore = 0;

        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .Where(team => team.IsValid)
            .ToList();
        if (teams.Count == 0)
        {
            teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("team_manager")
                .Where(team => team.IsValid)
                .ToList();
        }

        var terrorist = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.Terrorist);
        var counterTerrorist = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.CounterTerrorist);
        if (terrorist == null || counterTerrorist == null)
        {
            Console.WriteLine("[BotChat] team score entities are unavailable; taunt skipped");
            return false;
        }

        terroristScore = firstHalfOnly ? terrorist.ScoreFirstHalf : terrorist.Score;
        counterTerroristScore = firstHalfOnly ? counterTerrorist.ScoreFirstHalf : counterTerrorist.Score;
        return true;
    }

    private static bool RollPercent(int chancePercent)
    {
        if (chancePercent <= 0)
            return false;
        if (chancePercent >= 100)
            return true;
        return Random.Shared.Next(100) < chancePercent;
    }

    private static bool IsWarmupPeriod()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        // Treat an unavailable rules entity as warmup so a greeting cannot
        // leak into the transition before the first formal round.
        return rules?.WarmupPeriod ?? true;
    }

    private void ScheduleStartGreeting(int attempt = 0)
    {
        if (_startGreetingDone || _startGreetingScheduled)
            return;

        _startGreetingScheduled = true;
        AddTimer(0.75f, () =>
        {
            _startGreetingScheduled = false;
            if (_startGreetingDone || IsDeathmatch() || !Enabled.Value || !StartEnabled.Value)
                return;

            if (IsWarmupPeriod())
            {
                if (attempt < 20)
                    ScheduleStartGreeting(attempt + 1);
                return;
            }

            _startGreetingDone = true;
            if (!RollPercent(Config.Chat.StartMessageChancePercent))
            {
                Console.WriteLine("[BotChat] start greeting probability skipped");
                return;
            }

            var hasBot = Utilities.GetPlayers().Any(player =>
                player.IsValid && player.IsBot && !player.IsHLTV
                && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
                && !player.HasBeenControlledByPlayerThisRound);
            if (hasBot)
            {
                Console.WriteLine("[BotChat] sending start greeting through chat broadcast");
                SayAcrossTeams(_startMessages, uniform: true, baseDelay: 0.5f,
                    featureEnabled: () => StartEnabled.Value);
                return;
            }

            // With bot_quota 0 there is no controller that can execute `say`.
            // Still deliver the configured opening line at the same match point
            // instead of silently dropping the event.
            Console.WriteLine("[BotChat] no eligible BOT at start; sending system broadcast");
            BroadcastSystemMessage(PickMessage(_startMessages, uniform: true));
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Clears per-round state.
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _owedThanks.Clear();
        if (!_startGreetingDone && !_startGreetingScheduled
            && !IsDeathmatch() && Enabled.Value && StartEnabled.Value
            && !IsWarmupPeriod())
        {
            ScheduleStartGreeting();
        }
        return HookResult.Continue;
    }

    // Round end: every bot that got complimented this round and is still
    // alive replies thanks individually.
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (IsDeathmatch() || !Enabled.Value || !KillReactionsEnabled.Value)
            return HookResult.Continue;

        var aliveThankers = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid
                && p.IsBot
                && !p.IsHLTV
                && !p.HasBeenControlledByPlayerThisRound
                && p.PawnIsAlive
                && _owedThanks.Contains(PlayerStateKey(p)))
            .ToList();
        if (aliveThankers.Count == 0)
            return HookResult.Continue;

        // Every one of them talks, staggered like a natural conversation.
        float delay = 0.8f;
        foreach (var bot in aliveThankers)
        {
            string message = PickMessage(_thanksMessages, uniform: true);
            float scheduled = delay;
            ScheduleBotSay(bot, message, scheduled, () => KillReactionsEnabled.Value);
            delay += MinGap + (float)Random.Shared.NextDouble() * (MaxGap - MinGap);
        }

        return HookResult.Continue;
    }

    // Death reactions: the victim may compliment a clean kill or question a
    // suspicious kill. Only a real compliment creates a thanks reply.
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        // Log EVERY death to the server console so we can tell apart a missed
        // event from a failed probability roll.
        Console.WriteLine(
            $"[BotChat] death event: victim={victim?.PlayerName ?? "?"}(bot={victim?.IsBot}) " +
            $"killer={attacker?.PlayerName ?? "world"}(bot={attacker?.IsBot}) " +
            $"headshot={@event.Headshot} blind={@event.Attackerblind} smoke={@event.Thrusmoke} " +
            $"wall={@event.Penetrated} air={@event.Attackerinair}");

        if (IsDeathmatch() || IsWarmupPeriod() || !Enabled.Value)
            return HookResult.Continue;

        if (victim == null || !victim.IsValid || victim.IsHLTV)
            return HookResult.Continue;

        if (attacker == null || !attacker.IsValid || attacker.IsHLTV
            || attacker.Team == victim.Team || attacker.Slot == victim.Slot)
            return HookResult.Continue;

        if (KillReactionsEnabled.Value && victim.IsBot)
            HandleKillReactions(@event, victim, attacker);
        return HookResult.Continue;
    }

    // ns / suspicious-kill reactions; returns true when the victim spoke.
    private bool HandleKillReactions(
        EventPlayerDeath @event,
        CCSPlayerController victim,
        CCSPlayerController attacker)
    {
        string victimName = victim.PlayerName;
        string attackerName = attacker.PlayerName;

        // 1) Victim: react to a kill. Any kill can trigger; headshot raises
        // the chance. Suspicious-context pools are questions, not compliments.
        double chance = Config.Chat.KillReactionBaseChancePercent / 100.0;
        if (@event.Headshot)
            chance += Config.Chat.KillReactionHeadshotBonusPercent / 100.0;
        if (@event.Attackerblind)
            chance += Config.Chat.KillReactionContextBonusPercent / 100.0;
        if (@event.Thrusmoke)
            chance += Config.Chat.KillReactionContextBonusPercent / 100.0;
        if (@event.Penetrated > 0)
            chance += Config.Chat.KillReactionContextBonusPercent / 100.0;
        if (@event.Attackerinair)
            chance += Config.Chat.KillReactionContextBonusPercent / 100.0;
        chance = Math.Min(1.0, chance);

        double roll = Random.Shared.NextDouble();
        bool reactionTriggered = roll < chance;

        Console.WriteLine(
            $"[BotChat] ns roll: victim={victimName} killer={attackerName} " +
            $"chance={chance:P0} roll={roll:P2} -> {(reactionTriggered ? "reaction" : "silent")} " +
            $"(headshot={@event.Headshot} blind={@event.Attackerblind} smoke={@event.Thrusmoke} " +
            $"wall={@event.Penetrated} air={@event.Attackerinair})");

        bool reactionScheduled = false;
        if (reactionTriggered)
        {
            var reaction = PickVictimReaction(@event);
            reactionScheduled = ScheduleBotSay(victim, reaction.Message, ReactionDelay(),
                () => KillReactionsEnabled.Value);
            if (reactionScheduled && reaction.IsCompliment
                && attacker.IsBot
                && !attacker.HasBeenControlledByPlayerThisRound)
            {
                _owedThanks.Add(PlayerStateKey(attacker));
                Console.WriteLine(
                    $"[BotChat] owed-thanks += steamid {attacker.SteamID} ({attacker.PlayerName})");
            }
        }

        // 2) This victim, if it owed a thanks reply from an earlier ns, pays
        // it back now that it died (one reply only; the owed entry is
        // consumed so the round end won't repeat it).
        ulong victimKey = PlayerStateKey(victim);
        if (!reactionScheduled && _owedThanks.Contains(victimKey))
        {
            Console.WriteLine(
                $"[BotChat] death-thanks: {victimName} (slot {victim.Slot}) replies to {attackerName}");
            string thanks = PickMessage(_thanksMessages, uniform: true);
            if (ScheduleBotSay(victim, thanks, ReactionDelay(),
                () => KillReactionsEnabled.Value))
            {
                _owedThanks.Remove(victimKey);
                return true;
            }
        }

        return reactionScheduled;
    }

    // Picks 1..N random bots from a given list and staggers one message each.
    private void SayRandom(
        List<CCSPlayerController> bots,
        (string message, int weight)[] pool,
        bool uniform,
        float baseDelay,
        Func<bool>? featureEnabled = null)
    {
        if (bots.Count == 0)
            return;

        int count = Math.Min(AbsoluteMaxSpeakersPerTeam, Math.Min(Config.Chat.MaxSpeakersPerTeam, bots.Count));
        int speakers = Random.Shared.Next(1, count + 1);

        // Fisher-Yates shuffle, take the first `speakers`.
        for (int i = bots.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (bots[i], bots[j]) = (bots[j], bots[i]);
        }

        float delay = baseDelay;
        for (int i = 0; i < speakers; i++)
        {
            var bot = bots[i];
            string message = PickMessage(pool, uniform);
            float scheduled = delay;
            ScheduleBotSay(bot, message, scheduled, featureEnabled);
            delay += MinGap + (float)Random.Shared.NextDouble() * (MaxGap - MinGap);
        }
    }

    // Picks 1..5 random bots per team (humans and taken-over bots excluded)
    // and makes each say one message, staggered in time.
    private void SayAcrossTeams(
        (string message, int weight)[] pool,
        bool uniform,
        float baseDelay,
        Func<bool>? featureEnabled = null)
    {
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var bots = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Where(p => p.IsValid
                    && p.IsBot
                    && !p.IsHLTV
                    && p.Team == team
                    && !p.HasBeenControlledByPlayerThisRound)
                .ToList();
            if (bots.Count == 0)
                continue;

            SayRandom(bots, pool, uniform, baseDelay, featureEnabled);
        }
    }

    // Random reaction delay for death-related messages.
    private static float ReactionDelay() =>
        MinReactionDelay + (float)Random.Shared.NextDouble() * (MaxReactionDelay - MinReactionDelay);

    private static string PickMessage((string message, int weight)[] pool, bool uniform)
    {
        if (uniform)
            return pool[Random.Shared.Next(pool.Length)].message;

        int total = 0;
        foreach (var (_, weight) in pool)
            total += weight;

        int roll = Random.Shared.Next(total);
        foreach (var (message, weight) in pool)
        {
            if (roll < weight)
                return message;
            roll -= weight;
        }
        return pool[^1].message;
    }

    private (string Message, bool IsCompliment) PickVictimReaction(EventPlayerDeath @event)
    {
        var suspiciousPools = new List<(string message, int weight)[]>();
        if (@event.Thrusmoke) suspiciousPools.Add(_smokeMessages);
        if (@event.Penetrated > 0) suspiciousPools.Add(_wallbangMessages);
        if (@event.Attackerblind) suspiciousPools.Add(_blindKillMessages);
        if (@event.Attackerinair) suspiciousPools.Add(_airborneKillMessages);

        if (suspiciousPools.Count > 0)
        {
            var suspiciousPool = suspiciousPools[Random.Shared.Next(suspiciousPools.Count)];
            return (PickMessage(suspiciousPool, uniform: true), false);
        }

        if (@event.Headshot)
            return (PickMessage(_headshotMessages, uniform: true), true);

        return (PickMessage(_niceShotMessages, uniform: true), true);
    }

    private void LoadMessages()
    {
        var messages = BotChatMessageLoader.Load(ModuleDirectory, Config.Language);
        _startMessages = Uniform(messages.Start);
        _halfTimeMessages = Uniform(messages.HalfTime);
        _matchEndMessages = messages.MatchEnd.Select(entry => (entry.Key.Trim(), entry.Value)).ToArray();
        _dominantWinTauntMessages = Uniform(messages.Taunts.DominantWin);
        _halfTimeTauntMessages = Uniform(messages.Taunts.HalfTime);
        _afterWinTauntMessages = Uniform(messages.Taunts.AfterWin);
        _niceShotMessages = Uniform(messages.NiceShot);
        _headshotMessages = Uniform(messages.Headshot);
        _smokeMessages = Uniform(messages.ThroughSmoke);
        _wallbangMessages = Uniform(messages.Wallbang);
        _blindKillMessages = Uniform(messages.BlindKill);
        _airborneKillMessages = Uniform(messages.AirborneKill);
        _thanksMessages = Uniform(messages.Thanks);
        Console.WriteLine($"[BotChat] loaded language '{Config.Language}' from lang/{Config.Language}.yml");
    }

    private static (string message, int weight)[] Uniform(IEnumerable<string> messages) =>
        messages.Select(message => (message, 1)).ToArray();

    private bool IsDeathmatch()
    {
        var gameType = ConVar.Find("game_type");
        var gameMode = ConVar.Find("game_mode");
        if (gameType == null || gameMode == null)
            return false;

        return gameType.GetPrimitiveValue<int>() == 1
            && gameMode.GetPrimitiveValue<int>() == 2;
    }

    private bool ScheduleBotSay(
        CCSPlayerController bot,
        string message,
        float delay,
        Func<bool>? featureEnabled = null)
    {
        if (!bot.IsValid || !bot.IsBot || bot.IsHLTV || bot.HasBeenControlledByPlayerThisRound)
            return false;

        var speaker = new BotChatSpeaker(bot.SteamID, bot.Slot, bot.Handle, bot.PlayerName, bot.Team);
        var request = new BotChatMessageRequest(speaker, message);
        long reservation = _api.TryReserveSpeaker(speaker, delay + 0.5f);
        if (reservation == 0)
            return false;

        AddTimer(delay, () =>
        {
            try
            {
                if (featureEnabled?.Invoke() != false)
                    _api.TrySendBotMessage(request, reservation);
            }
            finally
            {
                _api.ReleaseSpeaker(speaker, reservation);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private static void BroadcastBotMessage(string name, CsTeam team, string message)
    {
        string line = $" {ChatColors.ForTeam(team)}{name}{ChatColors.Default}: {message}";
        Server.PrintToChatAll(line);
    }

    private static void BroadcastSystemMessage(string message)
    {
        Server.PrintToChatAll($" {ChatColors.Default}[BotChat]{ChatColors.Default}: {message}");
    }

    private sealed class BotChatApiImpl(BotChatPlugin owner) : IBotChatApi
    {
        private const double SpeakerQuietSeconds = 4.0;
        private readonly Dictionary<SpeakerKey, (long Token, double Until)> _speakerReservations = new();
        private readonly Dictionary<SpeakerKey, double> _speakerQuietUntil = new();
        private long _nextReservationToken;

        public int AbiVersion => 1;

        public void ResetReservations()
        {
            _speakerReservations.Clear();
            _speakerQuietUntil.Clear();
        }

        public long TryReserveSpeaker(BotChatSpeaker speaker, float holdSeconds)
        {
            if (speaker.ControllerHandle == nint.Zero || speaker.Slot < 0
                || holdSeconds < 0 || holdSeconds > 60)
                return 0;

            var key = SpeakerKey.From(speaker);
            double now = Server.CurrentTime;
            if (_speakerReservations.TryGetValue(key, out var reservation) && now < reservation.Until)
                return 0;
            if (_speakerQuietUntil.TryGetValue(key, out double quietUntil) && now < quietUntil)
                return 0;

            long token = ++_nextReservationToken;
            if (token == 0)
                token = ++_nextReservationToken;
            _speakerReservations[key] = (token, now + holdSeconds);
            return token;
        }

        public void ReleaseSpeaker(BotChatSpeaker speaker, long reservationToken)
        {
            var key = SpeakerKey.From(speaker);
            if (_speakerReservations.TryGetValue(key, out var reservation)
                && reservation.Token == reservationToken)
                _speakerReservations.Remove(key);
        }

        public bool TrySendBotMessage(BotChatMessageRequest request, long reservationToken)
        {
            if (request.Speaker.ControllerHandle == nint.Zero || string.IsNullOrWhiteSpace(request.Message))
                return false;
            var key = SpeakerKey.From(request.Speaker);
            if (!_speakerReservations.TryGetValue(key, out var reservation)
                || reservation.Token != reservationToken)
                return false;

            var bot = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .FirstOrDefault(p => p.IsValid && p.IsBot && !p.IsHLTV
                    && p.Handle == request.Speaker.ControllerHandle
                    && p.Slot == request.Speaker.Slot
                    && (request.Speaker.SteamId == 0 || p.SteamID == request.Speaker.SteamId)
                    && p.Team == request.Speaker.Team
                    && !p.HasBeenControlledByPlayerThisRound);
            if (bot == null || !owner.BotSaySnapshot(bot, request.Speaker.DisplayName, request.Speaker.Team, request.Message))
                return false;

            _speakerQuietUntil[key] = Server.CurrentTime + SpeakerQuietSeconds;
            return true;
        }

        private readonly record struct SpeakerKey(ulong SteamId, int Slot, nint ControllerHandle)
        {
            public static SpeakerKey From(BotChatSpeaker speaker) =>
                new(speaker.SteamId, speaker.Slot, speaker.ControllerHandle);
        }
    }

    private bool BotSaySnapshot(CCSPlayerController bot, string displayName, CsTeam team, string message)
    {
        if (IsDeathmatch() || IsWarmupPeriod() || !Enabled.Value || !bot.IsValid || !bot.IsBot
            || bot.IsHLTV || bot.Team != team || bot.HasBeenControlledByPlayerThisRound)
            return false;

        string safeName = SanitizeChatText(displayName, 64);
        string safeMessage = SanitizeChatText(message, 256);
        if (safeName.Length == 0 || safeMessage.Length == 0)
            return false;

        BroadcastBotMessage(safeName, team, safeMessage);
        Console.WriteLine($"[BotChat] sent '{safeMessage}' as {safeName} (slot {bot.Slot})");
        return true;
    }

    private static string SanitizeChatText(string value, int maxLength) =>
        new(value.Where(c => !char.IsControl(c)).Take(maxLength).ToArray());

    private static ulong PlayerStateKey(CCSPlayerController player) =>
        player.SteamID != 0 ? player.SteamID : 0xffff_0000_0000_0000UL | (uint)player.Slot;

}
