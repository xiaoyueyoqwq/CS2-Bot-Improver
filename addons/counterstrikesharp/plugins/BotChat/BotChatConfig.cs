using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace BotChat;

public sealed class BotChatConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;

    [JsonPropertyName("Language")]
    public string Language { get; set; } = "zh-CN";

    [JsonPropertyName("Chat")]
    public BotChatFrequencyConfig Chat { get; set; } = new();

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
