using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace Banter;

public sealed class BanterConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 1;
    [JsonPropertyName("Language")] public string Language { get; set; } = "zh-CN";
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("RevengeEnabled")] public bool RevengeEnabled { get; set; } = true;
    [JsonPropertyName("HighlightScoreThreshold")] public int HighlightScoreThreshold { get; set; } = 60;
    [JsonPropertyName("HighlightChancePercent")] public int HighlightChancePercent { get; set; } = 60;
    [JsonPropertyName("RevengeChancePercent")] public int RevengeChancePercent { get; set; } = 90;
    [JsonPropertyName("RedHotChancePercent")] public int RedHotChancePercent { get; set; } = 75;
    [JsonPropertyName("EnemyTauntChancePercent")] public int EnemyTauntChancePercent { get; set; } = 25;
    [JsonPropertyName("ResponseChancePercent")] public int ResponseChancePercent { get; set; } = 60;
    [JsonPropertyName("MaxChainLength")] public int MaxChainLength { get; set; } = 2;
    [JsonPropertyName("CooldownSeconds")] public int CooldownSeconds { get; set; } = 12;
    [JsonPropertyName("MaxMessagesPerRound")] public int MaxMessagesPerRound { get; set; } = 3;

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
