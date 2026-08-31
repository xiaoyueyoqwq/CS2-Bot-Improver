namespace BotBanter;

public enum FlagClaim
{
    ComebackBoast,   // 翻盘豪言 / "稳了"类
    KillVow,         // 单杀宣言 "这把不弄死你我不玩了"
    TauntEnemy,      // 嘲讽对面
    LagExcuse,       // "我卡了"
    LastGame         // "就打一把"
}

public sealed class Flag
{
    public string Speaker = string.Empty;
    public bool SpeakerIsBot;
    public int Round;
    public FlagClaim Claim;
    public int ExpiryRound;
    public bool Cashed;          // a flag is only ever busted once (拆两次就尬了)
    public string? Target;       // for KillVow: who the vow was against
    public string OriginalText = string.Empty;
}

public sealed class FlagLedger
{
    private readonly List<Flag> _flags = new();

    public void ResetMap() => _flags.Clear();

    public Flag Register(string speaker, bool isBot, int round, FlagClaim claim, string text, string? target = null)
    {
        var flag = new Flag
        {
            Speaker = speaker,
            SpeakerIsBot = isBot,
            Round = round,
            Claim = claim,
            ExpiryRound = claim == FlagClaim.LastGame ? int.MaxValue : round + 3,
            Target = target,
            OriginalText = text
        };
        _flags.Add(flag);
        return flag;
    }

    public IEnumerable<Flag> Active(int round) => _flags.Where(f => !f.Cashed && round <= f.ExpiryRound);

    public Flag? FindBustable(int round, Func<Flag, bool> predicate) =>
        Active(round).Where(predicate).OrderByDescending(f => f.Round).FirstOrDefault();

    // Player flag keyword whitelist — 宁可漏不可错 (chain doc §5).
    private static readonly string[] PlayerFlagKeywords = { "稳了", "赢定", "随便打", "包赢", "太菜了", "让你们" };

    public static bool LooksLikePlayerFlag(string chatText) =>
        PlayerFlagKeywords.Any(k => chatText.Contains(k, StringComparison.Ordinal));

    public static bool LooksLikeLagExcuse(string chatText) =>
        chatText.Contains("我卡了", StringComparison.Ordinal) || chatText.Contains("卡了 ", StringComparison.Ordinal);
}
