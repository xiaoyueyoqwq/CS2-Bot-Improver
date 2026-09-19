using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banter;

public sealed class BanterMessages
{
    public List<string> Highlight { get; set; } = [];
    public List<string> Revenge { get; set; } = [];
    public List<string> RedHot { get; set; } = [];
    public List<string> EnemyTaunt { get; set; } = [];
    public List<string> Response { get; set; } = [];
}

public static class BanterMessageLoader
{
    public static BanterMessages Load(string moduleDirectory, string language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException($"Invalid Banter language '{language}'.");
        string path = Path.Combine(moduleDirectory, "lang", language.Trim() + ".yml");
        if (!File.Exists(path)) throw new FileNotFoundException($"Banter language file was not found: {path}", path);
        var result = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<BanterMessages>(File.ReadAllText(path)) ?? new BanterMessages();
        Validate(result.Highlight, "highlight", path);
        Validate(result.Revenge, "revenge", path);
        Validate(result.RedHot, "red_hot", path);
        Validate(result.EnemyTaunt, "enemy_taunt", path);
        Validate(result.Response, "response", path);
        return result;
    }

    private static void Validate(List<string>? pool, string name, string path)
    {
        if (pool == null || pool.Count == 0) throw new InvalidDataException($"Banter pool '{name}' is empty in {path}.");
        for (int i = 0; i < pool.Count; i++)
        {
            pool[i] = pool[i]?.Trim() ?? "";
            if (pool[i].Length == 0) throw new InvalidDataException($"Banter pool '{name}' contains an empty entry in {path}.");
        }
    }
}
