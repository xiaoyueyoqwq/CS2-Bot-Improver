using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotBanter;

public sealed record AwardResult(string Key, string Title, string Winner, string Text, double Absurdity);

// 赛后颁奖: console gets the full show, chat gets a one-line teaser. MVP always last.
public static class AwardsCeremony
{
    private const float ClusterRadius = 300f;

    public static void Run(MatchState state, Lexicon lexicon, List<CCSPlayerController> humans, Random rng)
    {
        var players = state.Stats.Values.Where(s => s.Kills + s.Deaths + s.Damage > 0).ToList();
        if (players.Count == 0)
        {
            return;
        }

        var awards = ComputeAwards(state, lexicon, players);

        // Fun awards first (踩), 残局大师 (捧) second to last, MVP 压轴.
        var funAwards = awards.Where(a => a.Key is not ("clutch_master" or "mvp")).ToList();
        var clutch = awards.FirstOrDefault(a => a.Key == "clutch_master");
        var mvp = awards.First(a => a.Key == "mvp");

        // 3~5 awards per match, most absurd data first (v2 doc).
        var count = 3 + rng.Next(3);
        var show = funAwards.OrderByDescending(a => a.Absurdity).Take(count).ToList();
        if (clutch != null)
        {
            show.Add(clutch);
        }

        foreach (var human in humans)
        {
            human.PrintToConsole(string.Empty);
            human.PrintToConsole("══════════ 本 场 之 最 ══════════");
            foreach (var award in show)
            {
                human.PrintToConsole($"【{award.Title}】{award.Winner}");
                human.PrintToConsole($"   {award.Text}");
            }

            human.PrintToConsole(string.Empty);
            human.PrintToConsole("════════ 本 场 M V P ════════");
            human.PrintToConsole($"  {mvp.Winner}");
            human.PrintToConsole($"  {mvp.Text}");
            human.PrintToConsole($"  \"{lexicon.PickMvpPraise(state.Stats[mvp.Winner].IsBot)}\"");
            human.PrintToConsole("═════════════════════════════");
        }

        // Chat teaser: one or two of the funniest lines only.
        var teaser = show.FirstOrDefault();
        if (teaser != null)
        {
            Server.PrintToChatAll($" 本场{teaser.Title}：{teaser.Winner}，完整颁奖见控制台~");
        }

        Server.PrintToChatAll($" 本场MVP：{mvp.Winner}（控制台里还有货）");
    }

    private static List<AwardResult> ComputeAwards(MatchState state, Lexicon lexicon, List<PlayerStats> players)
    {
        var results = new List<AwardResult>();

        void Add(string key, string title, PlayerStats? winner, double absurdity, Dictionary<string, string> vars)
        {
            if (winner == null)
            {
                return;
            }

            var text = lexicon.PickAward(key, vars);
            if (text != null)
            {
                results.Add(new AwardResult(key, title, winner.Name, text, absurdity));
            }
        }

        // 最佳演员: most deaths + lowest K/D
        var actor = players.Where(p => p.Deaths >= 5).OrderByDescending(p => p.Deaths).ThenBy(p => p.Kd).FirstOrDefault();
        if (actor != null)
        {
            Add("best_actor", "最佳演员", actor, actor.Deaths,
                new() { ["name"] = actor.Name, ["deaths"] = actor.Deaths.ToString() });
        }

        // 狗托奖: most lucky kills
        var lucky = players.Where(p => p.LuckyKills >= 2).OrderByDescending(p => p.LuckyKills).FirstOrDefault();
        if (lucky != null)
        {
            Add("lucky_dog", "狗托奖", lucky, lucky.LuckyKills * 3,
                new() { ["name"] = lucky.Name, ["lucky"] = lucky.LuckyKills.ToString() });
        }

        // 劳模奖: highest damage but few kills
        var worker = players.Where(p => p.Damage >= 800 && p.Kills <= p.Damage / 300)
            .OrderByDescending(p => p.Damage).FirstOrDefault();
        if (worker != null)
        {
            Add("hard_worker", "劳模奖", worker, worker.Damage / 100.0,
                new() { ["name"] = worker.Name, ["dmg"] = worker.Damage.ToString(), ["kills"] = worker.Kills.ToString() });
        }

        // 快递员奖: most deaths on the same route (clustered death spots)
        var courier = players
            .Select(p => (p, cluster: LargestCluster(p.DeathSpots)))
            .Where(t => t.cluster >= 4)
            .OrderByDescending(t => t.cluster)
            .FirstOrDefault();
        if (courier.p != null)
        {
            Add("courier", "快递员奖", courier.p, courier.cluster * 2,
                new() { ["name"] = courier.p.Name, ["deaths"] = courier.cluster.ToString() });
        }

        // 钓鱼佬奖: most kills from the same spot
        var camper = players
            .Select(p => (p, cluster: LargestCluster(p.KillSpots)))
            .Where(t => t.cluster >= 4)
            .OrderByDescending(t => t.cluster)
            .FirstOrDefault();
        if (camper.p != null)
        {
            Add("camper", "钓鱼佬奖", camper.p, camper.cluster * 2,
                new() { ["name"] = camper.p.Name, ["kills"] = camper.cluster.ToString() });
        }

        // 经济学家奖: most money spent, worst return
        var economist = players.Where(p => p.MoneySpent >= 8000)
            .OrderByDescending(p => (double)p.MoneySpent / Math.Max(1, p.Kills)).FirstOrDefault();
        if (economist != null)
        {
            var per = economist.MoneySpent / Math.Max(1, economist.Kills);
            Add("economist", "经济学家奖", economist, per / 500.0, new()
            {
                ["name"] = economist.Name,
                ["spent"] = economist.MoneySpent.ToString(),
                ["kills"] = economist.Kills.ToString(),
                ["per"] = per.ToString()
            });
        }

        // 嘴强王者奖: most chat messages
        var mouth = players.Where(p => p.ChatMessages >= 5).OrderByDescending(p => p.ChatMessages).FirstOrDefault();
        if (mouth != null)
        {
            Add("loud_mouth", "嘴强王者奖", mouth, mouth.ChatMessages,
                new() { ["name"] = mouth.Name, ["msgs"] = mouth.ChatMessages.ToString(), ["kills"] = mouth.Kills.ToString() });
        }

        // 残局大师: most 1vX clutches won
        var clutcher = players.Where(p => p.ClutchWins >= 1).OrderByDescending(p => p.ClutchWins).FirstOrDefault();
        if (clutcher != null)
        {
            Add("clutch_master", "残局大师", clutcher, clutcher.ClutchWins * 5,
                new() { ["name"] = clutcher.Name, ["clutch"] = clutcher.ClutchWins.ToString() });
        }

        // 全场 MVP: composite rating, always present
        var mvp = players.OrderByDescending(Rating).First();
        var adr = state.RoundsPlayed > 0 ? mvp.Damage / state.RoundsPlayed : mvp.Damage;
        results.Add(new AwardResult(
            "mvp", "全场MVP", mvp.Name,
            $"K/D {mvp.Kills}/{mvp.Deaths} | ADR {adr} | 首杀 {mvp.FirstKills} 次 | 残局 {mvp.ClutchWins} 次",
            double.MaxValue));

        return results;
    }

    private static double Rating(PlayerStats p) =>
        p.Kills + p.Damage / 100.0 * 0.7 + p.ClutchWins * 2 + p.FirstKills * 0.5 + p.RoundsSurvived * 0.3 - p.Deaths * 0.3;

    private static int LargestCluster(List<(float X, float Y)> spots)
    {
        var best = 0;
        foreach (var center in spots)
        {
            var count = spots.Count(s =>
            {
                var dx = s.X - center.X;
                var dy = s.Y - center.Y;
                return dx * dx + dy * dy <= ClusterRadius * ClusterRadius;
            });
            best = Math.Max(best, count);
        }

        return best;
    }
}
