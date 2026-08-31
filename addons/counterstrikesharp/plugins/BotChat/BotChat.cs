// BotChat plugin: bots greet at match start, react to kills, and wrap up at
// match end. Configured taunts are sent only by bots on the leading team.
//
// Convars:
//   botchat_enabled              - master switch (default 1)
//   botchat_start_enabled        - greetings at match start (default 1)
//   botchat_halftime_enabled     - halftime messages (default 1)
//   botchat_end_enabled          - goodbyes and taunts at match end (default 1)
//   botchat_killreactions_enabled - kill reactions (ns / thanks) (default 1)
//   botchat_banter_enabled       - grudge/persona banter reactions (default 1)

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotChat;

public class BotChatPlugin : BasePlugin, IPluginConfig<BotChatConfig>
{
    public override string ModuleName => "BotChat";
    public override string ModuleVersion => "1.3.0";
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
    private (string message, int weight)[] _banterHighlightMessages = [];
    private (string message, int weight)[] _banterRevengeMessages = [];
    private (string message, int weight)[] _banterRedHotMessages = [];
    private (string message, int weight)[] _banterEnemyTauntMessages = [];
    private (string message, int weight)[] _banterResponseMessages = [];

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
    private bool _startGreetingDone;
    private bool _startGreetingScheduled;

    // Banter state: map-scoped grudge memory and persona casting, plus the
    // per-round budget and per-bot cooldowns that keep chatter human-paced.
    private readonly GrudgeGraph _grudges = new();
    private readonly PersonaAssigner _personas = new();
    private readonly Dictionary<ulong, double> _banterCooldownUntil = new();
    private readonly Dictionary<ulong, List<double>> _recentKillTimes = new();
    private int _banterThisRound;

    public FakeConVar<bool> Enabled = new("botchat_enabled", "Enable bot chat messages", true);
    public FakeConVar<bool> StartEnabled = new("botchat_start_enabled", "Bots greet at match start", true);
    public FakeConVar<bool> HalfTimeEnabled = new("botchat_halftime_enabled", "Bots chat at halftime", true);
    public FakeConVar<bool> EndEnabled = new("botchat_end_enabled", "Bots say goodbye at match end", true);
    public FakeConVar<bool> KillReactionsEnabled = new("botchat_killreactions_enabled", "Bots react to kills (ns / thanks)", true);
    public FakeConVar<bool> BanterEnabled = new("botchat_banter_enabled", "Bots hold grudges, take revenge and talk trash", true);

    public void OnConfigParsed(BotChatConfig config)
    {
        Config = config ?? new BotChatConfig();
        Config.Chat ??= new BotChatFrequencyConfig();
        Config.Chat.Normalize();
        Config.Taunts ??= new BotChatTauntConfig();
        Config.Taunts.Normalize();
        Config.Banter ??= new BotChatBanterConfig();
        Config.Banter.Normalize();
    }

    public override void Load(bool hotReload)
    {
        LoadMessages();
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _endSaid = false;
            _startGreetingDone = false;
            _startGreetingScheduled = false;
            _grudges.ResetMap();
            _personas.Reset();
            _banterCooldownUntil.Clear();
            _recentKillTimes.Clear();
            _banterThisRound = 0;
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
        _endSaid = false;
        _startGreetingDone = false;
        _startGreetingScheduled = false;
        // The opening greeting is scheduled from the first formal round below.
        // BeginNewMatch can occur during warmup, before teams and BOTs exist.
        return HookResult.Continue;
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
        // Tilt does not survive the side switch.
        _grudges.ResetHalf();

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
        _banterThisRound = 0;
        _recentKillTimes.Clear();
        _grudges.DecayRound();
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
                && _owedThanks.Contains(p.SteamID))
            .ToList();
        if (aliveThankers.Count == 0)
            return HookResult.Continue;

        // Every one of them talks, staggered like a natural conversation.
        float delay = 0.8f;
        foreach (var bot in aliveThankers)
        {
            string message = PickMessage(_thanksMessages, uniform: true);
            float scheduled = delay;
            AddTimer(scheduled, () => BotSay(bot, message,
                () => KillReactionsEnabled.Value));
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

        if (IsDeathmatch() || !Enabled.Value)
            return HookResult.Continue;

        if (victim == null || !victim.IsValid || victim.IsHLTV)
            return HookResult.Continue;

        if (attacker == null || !attacker.IsValid || attacker.IsHLTV
            || attacker.Team == victim.Team || attacker.Slot == victim.Slot)
            return HookResult.Continue;

        bool victimSpoke = false;
        if (KillReactionsEnabled.Value && victim.IsBot)
            victimSpoke = HandleKillReactions(@event, victim, attacker);

        HandleBanterOnDeath(@event, victim, attacker, victimSpoke);
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

        if (reactionTriggered)
        {
            var reaction = PickVictimReaction(@event);
            if (reaction.IsCompliment
                && attacker.IsBot
                && !attacker.HasBeenControlledByPlayerThisRound)
            {
                _owedThanks.Add(attacker.SteamID);
                Console.WriteLine(
                    $"[BotChat] owed-thanks += steamid {attacker.SteamID} ({attacker.PlayerName})");
            }
            AddTimer(ReactionDelay(), () => BotSay(victim, reaction.Message,
                () => KillReactionsEnabled.Value));
        }

        // 2) This victim, if it owed a thanks reply from an earlier ns, pays
        // it back now that it died (one reply only; the owed entry is
        // consumed so the round end won't repeat it).
        if (_owedThanks.Remove(victim.SteamID))
        {
            Console.WriteLine(
                $"[BotChat] death-thanks: {victimName} (slot {victim.Slot}) replies to {attackerName}");
            string thanks = PickMessage(_thanksMessages, uniform: true);
            AddTimer(ReactionDelay(), () => BotSay(victim, thanks,
                () => KillReactionsEnabled.Value));
            return true;
        }

        return reactionTriggered;
    }

    // Grudge-aware banter. Exactly one banter line may fire per death, picked
    // by priority: revenge > red-hot > highlight praise > enemy taunt. A
    // taunted bot may fire back once (chain of two at most).
    private void HandleBanterOnDeath(
        EventPlayerDeath @event,
        CCSPlayerController victim,
        CCSPlayerController attacker,
        bool victimSpoke)
    {
        if (!BanterEnabled.Value || !Config.Banter.Enabled)
            return;

        ulong attackerId = attacker.SteamID;
        ulong victimId = victim.SteamID;
        string attackerName = attacker.PlayerName;
        string victimName = victim.PlayerName;
        double now = Server.CurrentTime;

        bool isRevenge = Config.Banter.RevengeEnabled && _grudges.IsRevenge(attackerId, victimId);
        if (isRevenge)
            _grudges.ClearRedHot(attackerId, victimId);

        var edge = _grudges.RecordKill(attackerId, victimId);
        int streak = RecordKillTime(attackerId, now);

        if (_banterThisRound >= Config.Banter.MaxMessagesPerRound)
            return;

        // 1) Revenge: the attacker was red-hot at this victim and finally
        // got them back. Red-hot bots never shut up, so no silence gate.
        if (isRevenge && attacker.IsBot && !attacker.HasBeenControlledByPlayerThisRound
            && RollPercent(Config.Banter.RevengeChancePercent)
            && TryBanterSay(attacker, _banterRevengeMessages, victimName, attackerName, now))
        {
            Console.WriteLine($"[BotChat] banter revenge: {attackerName} -> {victimName}");
            return;
        }

        // 2) Red-hot: the victim has now died to the same tormentor enough
        // times to tilt. Fires once, at the moment the grudge boils over.
        if (victim.IsBot && !victim.HasBeenControlledByPlayerThisRound && !victimSpoke
            && edge.Kills == 3 && _grudges.IsRedHot(victimId, attackerId)
            && PersonaTraits.SpeaksWhenKilled(_personas.Get(victimId))
            && RollPercent(Config.Banter.RedHotChancePercent)
            && TryBanterSay(victim, _banterRedHotMessages, attackerName, victimName, now))
        {
            Console.WriteLine($"[BotChat] banter red-hot: {victimName} at {attackerName} ({edge.Kills} deaths)");
            return;
        }

        // 3) Highlight: a spectacular kill draws a comment from a bystander.
        int score = HighlightScorer.Score(new KillContext
        {
            Weapon = @event.Weapon,
            Headshot = @event.Headshot,
            Noscope = @event.Noscope,
            ThruSmoke = @event.Thrusmoke,
            AttackerBlind = @event.Attackerblind,
            Penetrated = @event.Penetrated,
            Distance = @event.Distance,
            AttackerInAir = @event.Attackerinair,
            AttackerHp = attacker.PlayerPawn.Value?.Health ?? 0,
            KillStreakInWindow = streak
        });
        if (score >= Config.Banter.HighlightScoreThreshold
            && RollPercent(Config.Banter.HighlightChancePercent))
        {
            var commentator = PickBanterSpeaker(now, excludeA: attacker.Slot, excludeB: victim.Slot);
            if (commentator != null
                && TryBanterSay(commentator, _banterHighlightMessages, attackerName, commentator.PlayerName, now))
            {
                Console.WriteLine($"[BotChat] banter highlight: {commentator.PlayerName} on {attackerName}'s kill (score {score})");
                return;
            }
        }

        // 4) Enemy taunt: the killer rubs it in, more likely on a repeat
        // kill. The taunted bot may fire back once.
        int tauntChance = Config.Banter.EnemyTauntChancePercent + (edge.Kills >= 2 ? 20 : 0);
        if (attacker.IsBot && !attacker.HasBeenControlledByPlayerThisRound
            && RollPercent(Math.Min(100, tauntChance))
            && !OnBanterCooldown(attackerId, now)
            && !RollSilence(_personas.Get(attackerId)))
        {
            if (TryBanterSay(attacker, _banterEnemyTauntMessages, victimName, attackerName, now))
            {
                Console.WriteLine($"[BotChat] banter taunt: {attackerName} -> {victimName}");
                if (Config.Banter.MaxChainLength >= 2 && victim.IsBot
                    && !victim.HasBeenControlledByPlayerThisRound
                    && PersonaTraits.SpeaksWhenKilled(_personas.Get(victimId))
                    && RollPercent(Config.Banter.ResponseChancePercent))
                {
                    ScheduleBanterResponse(victimId, attackerName, now);
                }
            }
        }
    }

    // Tracks the attacker's kill timestamps inside a 5 second multi-kill
    // window; returns the streak length including this kill.
    private int RecordKillTime(ulong attackerId, double now)
    {
        if (!_recentKillTimes.TryGetValue(attackerId, out var times))
        {
            times = new List<double>();
            _recentKillTimes[attackerId] = times;
        }

        times.RemoveAll(t => now - t > 5.0);
        times.Add(now);
        return times.Count;
    }

    private bool OnBanterCooldown(ulong steamId, double now) =>
        _banterCooldownUntil.TryGetValue(steamId, out double until) && now < until;

    private static bool RollSilence(Persona persona) =>
        Random.Shared.NextDouble() < PersonaTraits.SilenceRate(persona);

    // Sends one banter line as `speaker` after a human-feeling delay. Applies
    // the per-bot cooldown and the per-round budget; the delayed send resolves
    // the speaker by SteamID again so a disconnected or taken-over bot (or a
    // map change) invalidates the message.
    private bool TryBanterSay(
        CCSPlayerController speaker,
        (string message, int weight)[] pool,
        string otherName,
        string speakerName,
        double now)
    {
        if (pool.Length == 0)
            return false;
        if (!speaker.IsBot || speaker.HasBeenControlledByPlayerThisRound)
            return false;
        if (OnBanterCooldown(speaker.SteamID, now))
            return false;

        string message = FormatBanter(PickMessage(pool, uniform: true), otherName, speakerName);
        ulong speakerId = speaker.SteamID;
        _banterCooldownUntil[speakerId] = now + Config.Banter.CooldownSeconds;
        _banterThisRound++;

        AddTimer(ReactionDelay(), () =>
        {
            var bot = ResolveBot(speakerId);
            if (bot == null || !BanterEnabled.Value || !Config.Banter.Enabled)
                return;
            BotSay(bot, message);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private void ScheduleBanterResponse(ulong responderId, string tauntedByName, double now)
    {
        if (_banterResponseMessages.Length == 0
            || _banterThisRound >= Config.Banter.MaxMessagesPerRound
            || OnBanterCooldown(responderId, now))
            return;

        _banterCooldownUntil[responderId] = now + Config.Banter.CooldownSeconds;
        _banterThisRound++;

        // A beat after the taunt lands, so the exchange reads taunt -> reply.
        float delay = ReactionDelay() + MinGap
            + (float)Random.Shared.NextDouble() * (MaxGap - MinGap);
        AddTimer(delay, () =>
        {
            var bot = ResolveBot(responderId);
            if (bot == null || !BanterEnabled.Value || !Config.Banter.Enabled)
                return;
            BotSay(bot, FormatBanter(PickMessage(_banterResponseMessages, uniform: true),
                tauntedByName, bot.PlayerName));
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // {other}: the other party in the exchange; {self}: the speaker.
    private static string FormatBanter(string template, string otherName, string speakerName) =>
        template.Replace("{other}", otherName).Replace("{self}", speakerName);

    private static CCSPlayerController? ResolveBot(ulong steamId) =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .FirstOrDefault(p => p.IsValid && p.IsBot && !p.IsHLTV
                && p.SteamID == steamId && !p.HasBeenControlledByPlayerThisRound);

    // A random eligible bot to comment on someone else's play, filtered by
    // cooldown and persona silence.
    private CCSPlayerController? PickBanterSpeaker(double now, int excludeA, int excludeB)
    {
        var candidates = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid && p.IsBot && !p.IsHLTV
                && !p.HasBeenControlledByPlayerThisRound
                && (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist)
                && p.Slot != excludeA && p.Slot != excludeB
                && !OnBanterCooldown(p.SteamID, now))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var pick = candidates[Random.Shared.Next(candidates.Count)];
        return RollSilence(_personas.Get(pick.SteamID)) ? null : pick;
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
            AddTimer(scheduled, () => BotSay(bot, message, featureEnabled));
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
        _banterHighlightMessages = Uniform(messages.Banter.Highlight);
        _banterRevengeMessages = Uniform(messages.Banter.Revenge);
        _banterRedHotMessages = Uniform(messages.Banter.RedHot);
        _banterEnemyTauntMessages = Uniform(messages.Banter.EnemyTaunt);
        _banterResponseMessages = Uniform(messages.Banter.Response);
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

    private void BotSay(CCSPlayerController bot, string message, Func<bool>? featureEnabled = null)
    {
        if (IsDeathmatch()
            || !Enabled.Value
            || featureEnabled?.Invoke() == false
            || bot == null
            || !bot.IsValid
            || !bot.IsBot
            || bot.IsHLTV
            || bot.HasBeenControlledByPlayerThisRound)
        {
            Console.WriteLine($"[BotChat] say skipped: speaker no longer eligible");
            return;
        }
        BroadcastBotMessage(bot.PlayerName, bot.Team, message);
        Console.WriteLine($"[BotChat] sent '{message}' as {bot.PlayerName} (slot {bot.Slot})");
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

}
