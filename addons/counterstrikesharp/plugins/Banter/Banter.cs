using BotChatApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Banter;

public sealed class BanterPlugin : BasePlugin, IPluginConfig<BanterConfig>
{
    public override string ModuleName => "Banter";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Fimallory";
    public override string ModuleDescription => "Optional grudge and persona banter for BotChat";

    public BanterConfig Config { get; set; } = new();
    public FakeConVar<bool> Enabled = new("banter_enabled", "Enable bot banter", true);
    private BanterMessages _messages = new();
    private readonly GrudgeGraph _grudges = new();
    private readonly PersonaAssigner _personas = new();
    private readonly Dictionary<ulong, double> _cooldowns = new();
    private readonly Dictionary<ulong, List<double>> _killTimes = new();
    private int _roundMessages;
    private int _pendingMessages;
    private int _generation;
    private bool _apiWarningLogged;

    public void OnConfigParsed(BanterConfig config)
    {
        Config = config ?? new BanterConfig();
        Config.Version = 1;
        Config.Normalize();
    }

    public override void Load(bool hotReload)
    {
        try { _messages = BanterMessageLoader.Load(ModuleDirectory, Config.Language); }
        catch (Exception ex)
        {
            Console.WriteLine($"[Banter] disabled: {ex.Message}");
            Config.Enabled = false;
        }
        RegisterListener<Listeners.OnMapStart>(_ => ResetMap());
        RegisterEventHandler<EventBeginNewMatch>((_, _) => { ResetMap(); return HookResult.Continue; });
        RegisterEventHandler<EventStartHalftime>((_, _) => { _grudges.ResetHalf(); return HookResult.Continue; });
        RegisterEventHandler<EventRoundStart>((_, _) => { BeginRound(); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        Console.WriteLine(Config.Enabled
            ? "[Banter] loaded as optional BotChat enhancement"
            : "[Banter] loaded in disabled state");
    }

    private void ResetMap()
    {
        _generation++;
        _grudges.ResetMap();
        _personas.Reset();
        _cooldowns.Clear();
        _killTimes.Clear();
        _roundMessages = 0;
        _pendingMessages = 0;
    }

    private void BeginRound()
    {
        _generation++;
        _roundMessages = 0;
        _pendingMessages = 0;
        _killTimes.Clear();
        _grudges.DecayRound();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!Enabled.Value || !Config.Enabled || IsDeathmatch() || IsWarmupPeriod())
            return HookResult.Continue;
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || attacker == null || !victim.IsValid || !attacker.IsValid || victim.IsHLTV || attacker.IsHLTV
            || victim.Team == attacker.Team || victim.Slot == attacker.Slot)
            return HookResult.Continue;

        int generation = _generation;
        var death = new DeathSnapshot(
            victim.SteamID, victim.Slot, victim.Handle,
            attacker.SteamID, attacker.Slot, attacker.Handle,
            @event.Weapon, @event.Headshot, @event.Noscope, @event.Thrusmoke,
            @event.Attackerblind, @event.Penetrated, @event.Distance, @event.Attackerinair);
        Server.NextFrame(() =>
        {
            if (generation == _generation)
                HandleDeath(death);
        });
        return HookResult.Continue;
    }

    private void HandleDeath(DeathSnapshot death)
    {
        if (!Enabled.Value || !Config.Enabled || IsDeathmatch() || IsWarmupPeriod())
            return;
        var victim = ResolvePlayer(death.VictimSteamId, death.VictimSlot, death.VictimHandle);
        var attacker = ResolvePlayer(death.AttackerSteamId, death.AttackerSlot, death.AttackerHandle);
        if (victim == null || attacker == null || victim.Team == attacker.Team)
            return;

        ulong killerId = PlayerStateKey(attacker), victimId = PlayerStateKey(victim);
        double now = Server.CurrentTime;
        bool revenge = Config.RevengeEnabled && _grudges.IsRevenge(killerId, victimId);
        var edge = _grudges.RecordKill(killerId, victimId);
        int streak = RecordKill(killerId, now);
        if (_roundMessages + _pendingMessages >= Config.MaxMessagesPerRound)
            return;

        if (revenge && attacker.IsBot && Roll(Config.RevengeChancePercent)
            && TrySay(attacker, _messages.Revenge, victim.PlayerName, now,
                sent => { if (sent) _grudges.ClearRedHot(killerId, victimId); }))
        {
            return;
        }

        if (victim.IsBot && edge.Kills >= 3 && _grudges.IsRedHot(victimId, killerId)
            && PersonaTraits.SpeaksWhenKilled(_personas.Get(victimId)) && Roll(Config.RedHotChancePercent)
            && TrySay(victim, _messages.RedHot, attacker.PlayerName, now,
                sent => { if (sent) _grudges.ClearRedHot(victimId, killerId); }))
        {
            return;
        }

        int score = HighlightScorer.Score(new KillContext
        {
            Weapon = death.Weapon, Headshot = death.Headshot, Noscope = death.Noscope,
            ThruSmoke = death.ThruSmoke, AttackerBlind = death.AttackerBlind,
            Penetrated = death.Penetrated, Distance = death.Distance,
            AttackerInAir = death.AttackerInAir, AttackerHp = attacker.PlayerPawn.Value?.Health ?? 0,
            KillStreakInWindow = streak
        });
        if (score >= Config.HighlightScoreThreshold && Roll(Config.HighlightChancePercent))
        {
            var speaker = PickSpeaker(attacker.Slot, victim.Slot, now);
            if (speaker != null) TrySay(speaker, _messages.Highlight, attacker.PlayerName, now);
            return;
        }

        int chance = Math.Min(100, Config.EnemyTauntChancePercent + (edge.Kills >= 2 ? 20 : 0));
        if (attacker.IsBot && Roll(chance) && !Silent(_personas.Get(killerId)))
        {
            bool victimIsBot = victim.IsBot;
            string attackerName = attacker.PlayerName;
            TrySay(attacker, _messages.EnemyTaunt, victim.PlayerName, now, sent =>
            {
                if (sent && Config.MaxChainLength >= 2 && victimIsBot
                    && PersonaTraits.SpeaksWhenKilled(_personas.Get(victimId))
                    && Roll(Config.ResponseChancePercent))
                    ScheduleResponse(victimId, attackerName, Server.CurrentTime);
            });
        }
    }

    private bool TrySay(
        CCSPlayerController speaker,
        List<string> pool,
        string other,
        double now,
        Action<bool>? completed = null,
        float? delayOverride = null)
    {
        ulong speakerKey = PlayerStateKey(speaker);
        if (pool.Count == 0 || _roundMessages + _pendingMessages >= Config.MaxMessagesPerRound || !speaker.IsBot
            || speaker.HasBeenControlledByPlayerThisRound || OnCooldown(speakerKey, now)) return false;
        string message = Format(pool[Random.Shared.Next(pool.Count)], other, speaker.PlayerName);
        var identity = new BotChatSpeaker(
            speaker.SteamID, speaker.Slot, speaker.Handle, Sanitize(speaker.PlayerName), speaker.Team);
        var request = new BotChatMessageRequest(identity, message);
        var api = GetApi();
        float delay = delayOverride ?? ReactionDelay();
        if (api == null || api.AbiVersion != 1)
            return false;
        long reservation = api.TryReserveSpeaker(identity, delay + 0.5f);
        if (reservation == 0)
            return false;

        _pendingMessages++;
        int generation = _generation;
        AddTimer(delay, () =>
        {
            bool sent = false;
            try
            {
                if (generation == _generation && Enabled.Value && Config.Enabled)
                    sent = GetApi()?.TrySendBotMessage(request, reservation) == true;
                if (sent)
                {
                    _cooldowns[speakerKey] = Server.CurrentTime + Config.CooldownSeconds;
                    _roundMessages++;
                }
            }
            finally
            {
                api.ReleaseSpeaker(identity, reservation);
                if (generation == _generation)
                    _pendingMessages = Math.Max(0, _pendingMessages - 1);
                completed?.Invoke(sent);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private void ScheduleResponse(ulong responderId, string tauntedBy, double now)
    {
        var responder = Resolve(responderId);
        if (responder == null || _messages.Response.Count == 0 || OnCooldown(responderId, now)) return;
        float delay = 1.2f + (float)Random.Shared.NextDouble() * 1.8f;
        TrySay(responder, _messages.Response, tauntedBy, now, delayOverride: delay);
    }

    private IBotChatApi? GetApi()
    {
        try { return BotChatCapability.Cap.Get(); }
        catch
        {
            if (!_apiWarningLogged)
            {
                _apiWarningLogged = true;
                Console.WriteLine("[Banter] BotChat capability is unavailable; banter will remain idle");
            }
            return null;
        }
    }

    private CCSPlayerController? PickSpeaker(int excludeA, int excludeB, double now) =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid && p.IsBot && !p.IsHLTV && p.Slot != excludeA && p.Slot != excludeB
                && (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist)
                && !OnCooldown(PlayerStateKey(p), now))
            .OrderBy(_ => Random.Shared.Next()).FirstOrDefault(p => !Silent(_personas.Get(PlayerStateKey(p))));

    private CCSPlayerController? Resolve(ulong id) => Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
        .FirstOrDefault(p => p.IsValid && p.IsBot && !p.IsHLTV
            && PlayerStateKey(p) == id && !p.HasBeenControlledByPlayerThisRound);
    private static CCSPlayerController? ResolvePlayer(ulong id, int slot, nint handle) =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .FirstOrDefault(p => p.IsValid && !p.IsHLTV && p.Handle == handle
                && p.Slot == slot && (id == 0 || p.SteamID == id));
    private bool OnCooldown(ulong id, double now) => _cooldowns.TryGetValue(id, out var until) && now < until;
    private int RecordKill(ulong id, double now) { if (!_killTimes.TryGetValue(id, out var t)) _killTimes[id] = t = []; t.RemoveAll(x => now - x > 5); t.Add(now); return t.Count; }
    private static bool Silent(Persona p) => Random.Shared.NextDouble() < PersonaTraits.SilenceRate(p);
    private static bool Roll(int chance) => chance > 0 && (chance >= 100 || Random.Shared.Next(100) < chance);
    private static float ReactionDelay() => 1.2f + (float)Random.Shared.NextDouble() * 2.3f;
    private static string Format(string text, string other, string self) => text.Replace("{other}", Sanitize(other)).Replace("{self}", Sanitize(self));
    private static string Sanitize(string value) => new(value.Where(c => c >= 32 && c != '\n' && c != '\r' && c != '"' && c != ';').ToArray());
    private static ulong PlayerStateKey(CCSPlayerController player) =>
        player.SteamID != 0 ? player.SteamID : 0xffff_0000_0000_0000UL | (uint)player.Slot;
    private static bool IsWarmupPeriod() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? true;
    private static bool IsDeathmatch() => ConVar.Find("game_type")?.GetPrimitiveValue<int>() == 1 && ConVar.Find("game_mode")?.GetPrimitiveValue<int>() == 2;

    private sealed record DeathSnapshot(
        ulong VictimSteamId,
        int VictimSlot,
        nint VictimHandle,
        ulong AttackerSteamId,
        int AttackerSlot,
        nint AttackerHandle,
        string Weapon,
        bool Headshot,
        bool Noscope,
        bool ThruSmoke,
        bool AttackerBlind,
        int Penetrated,
        float Distance,
        bool AttackerInAir);
}
