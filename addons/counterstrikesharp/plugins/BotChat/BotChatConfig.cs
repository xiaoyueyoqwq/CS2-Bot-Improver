using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace BotChat;

public sealed class BotChatConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    [JsonPropertyName("Language")]
    public string Language { get; set; } = "zh-CN";

    [JsonPropertyName("Chat")]
    public BotChatFrequencyConfig Chat { get; set; } = new();

    [JsonPropertyName("Taunts")]
    public BotChatTauntConfig Taunts { get; set; } = new();

    [JsonPropertyName("Banter")]
    public BotChatBanterConfig Banter { get; set; } = new();
}

public sealed class BotChatFrequencyConfig
{
    [JsonPropertyName("MaxSpeakersPerTeam")]
    public int MaxSpeakersPerTeam { get; set; } = 2;

    [JsonPropertyName("StartMessageChancePercent")]
    public int StartMessageChancePercent { get; set; } = 85;

    [JsonPropertyName("HalftimeMessageChancePercent")]
    public int HalftimeMessageChancePercent { get; set; } = 60;

    [JsonPropertyName("MatchEndMessageChancePercent")]
    public int MatchEndMessageChancePercent { get; set; } = 85;

    [JsonPropertyName("KillReactionBaseChancePercent")]
    public int KillReactionBaseChancePercent { get; set; } = 20;

    [JsonPropertyName("KillReactionHeadshotBonusPercent")]
    public int KillReactionHeadshotBonusPercent { get; set; } = 15;

    [JsonPropertyName("KillReactionContextBonusPercent")]
    public int KillReactionContextBonusPercent { get; set; } = 25;

    public void Normalize()
    {
        MaxSpeakersPerTeam = Math.Clamp(MaxSpeakersPerTeam, 1, 5);
        StartMessageChancePercent = Math.Clamp(StartMessageChancePercent, 0, 100);
        HalftimeMessageChancePercent = Math.Clamp(HalftimeMessageChancePercent, 0, 100);
        MatchEndMessageChancePercent = Math.Clamp(MatchEndMessageChancePercent, 0, 100);
        KillReactionBaseChancePercent = Math.Clamp(KillReactionBaseChancePercent, 0, 100);
        KillReactionHeadshotBonusPercent = Math.Clamp(KillReactionHeadshotBonusPercent, 0, 100);
        KillReactionContextBonusPercent = Math.Clamp(KillReactionContextBonusPercent, 0, 100);
    }
}

public sealed class BotChatTauntConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("DominantWinScoreGapThreshold")]
    public int DominantWinScoreGapThreshold { get; set; } = 5;

    [JsonPropertyName("DominantWinChancePercent")]
    public int DominantWinChancePercent { get; set; } = 40;

    [JsonPropertyName("HalftimeScoreGapThreshold")]
    public int HalftimeScoreGapThreshold { get; set; } = 3;

    [JsonPropertyName("HalftimeChancePercent")]
    public int HalftimeChancePercent { get; set; } = 35;

    [JsonPropertyName("AfterWinChancePercent")]
    public int AfterWinChancePercent { get; set; } = 30;

    public void Normalize()
    {
        DominantWinScoreGapThreshold = Math.Max(0, DominantWinScoreGapThreshold);
        HalftimeScoreGapThreshold = Math.Max(0, HalftimeScoreGapThreshold);
        DominantWinChancePercent = Math.Clamp(DominantWinChancePercent, 0, 100);
        HalftimeChancePercent = Math.Clamp(HalftimeChancePercent, 0, 100);
        AfterWinChancePercent = Math.Clamp(AfterWinChancePercent, 0, 100);
    }
}

public sealed class BotChatBanterConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("RevengeEnabled")]
    public bool RevengeEnabled { get; set; } = true;

    [JsonPropertyName("HighlightScoreThreshold")]
    public int HighlightScoreThreshold { get; set; } = 60;

    [JsonPropertyName("HighlightChancePercent")]
    public int HighlightChancePercent { get; set; } = 60;

    [JsonPropertyName("RevengeChancePercent")]
    public int RevengeChancePercent { get; set; } = 90;

    [JsonPropertyName("RedHotChancePercent")]
    public int RedHotChancePercent { get; set; } = 75;

    [JsonPropertyName("EnemyTauntChancePercent")]
    public int EnemyTauntChancePercent { get; set; } = 25;

    [JsonPropertyName("ResponseChancePercent")]
    public int ResponseChancePercent { get; set; } = 60;

    [JsonPropertyName("MaxChainLength")]
    public int MaxChainLength { get; set; } = 2;

    [JsonPropertyName("CooldownSeconds")]
    public int CooldownSeconds { get; set; } = 12;

    [JsonPropertyName("MaxMessagesPerRound")]
    public int MaxMessagesPerRound { get; set; } = 3;

    public void Normalize()
    {
        HighlightScoreThreshold = Math.Max(0, HighlightScoreThreshold);
        HighlightChancePercent = Math.Clamp(HighlightChancePercent, 0, 100);
        RevengeChancePercent = Math.Clamp(RevengeChancePercent, 0, 100);
        RedHotChancePercent = Math.Clamp(RedHotChancePercent, 0, 100);
        EnemyTauntChancePercent = Math.Clamp(EnemyTauntChancePercent, 0, 100);
        ResponseChancePercent = Math.Clamp(ResponseChancePercent, 0, 100);
        MaxChainLength = Math.Clamp(MaxChainLength, 1, 2);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 300);
        MaxMessagesPerRound = Math.Clamp(MaxMessagesPerRound, 1, 10);
    }
}
