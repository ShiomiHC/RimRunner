using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal static class EngineCommand
{
    private const int Schema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static int Execute(IReadOnlyList<string> words, bool json, bool full, Func<string, int> usage)
    {
        string action = words[0];
        return action switch
        {
            "find" => Find(Load(Authoring), [.. words.Skip(1)], full),
            "fixtures" => Fixtures(Load(Authoring), json),
            _ => throw new UnreachableException($"未知的 engine 子动作：{action}"),
        };
    }

    private static string Authoring(EnginePackage package) => package.AuthoringPath;

    private static JsonElement Load(Func<EnginePackage, string> pathOf)
    {
        string path = pathOf(EnginePackage.Locate().AssertComplete());
        JsonElement root;
        try
        {
            using JsonDocument document = StrictJson.ParseObject(File.ReadAllText(path));
            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new StoreException($"无法读取 {path}：{exception.Message}");
        }

        if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
            || schema.GetInt32() != Schema)
        {
            throw new StoreException($"{path} 的 schema 不是 {Schema}");
        }

        return root;
    }

    private sealed record TypeEntry(string Name, string Kind, string File, int Line, int OtherFiles, JsonElement Doc, List<MemberEntry> Members);

    private sealed record MemberEntry(TypeEntry Owner, JsonElement Element, string File, string Name)
    {
        internal string Signature => Element.GetProperty("signature").GetString() ?? "";
        internal int Line => Element.GetProperty("line").GetInt32();
    }

    private static List<TypeEntry> Types(JsonElement root)
    {
        List<TypeEntry> types = [];
        foreach (IGrouping<string, JsonElement> parts in root.GetProperty("fixtures").EnumerateArray()
            .GroupBy(unit => unit.GetProperty("name").GetString() ?? ""))
        {
            JsonElement[] units = [.. parts];
            JsonElement home = units.FirstOrDefault(unit => Path.GetFileNameWithoutExtension(unit.GetProperty("file").GetString()) == parts.Key);
            if (home.ValueKind == JsonValueKind.Undefined) home = units[0];
            var type = new TypeEntry(parts.Key, Text(home, "kind") ?? "", home.GetProperty("file").GetString() ?? "",
                home.GetProperty("line").GetInt32(), units.Length - 1, home, []);
            foreach (JsonElement unit in units)
            {
                string file = unit.GetProperty("file").GetString() ?? "";
                foreach (JsonElement member in unit.GetProperty("members").EnumerateArray())
                    type.Members.Add(new MemberEntry(type, member, file, MethodName(member) ?? LastWord(member)));
            }
            types.Add(type);
        }
        return types;
    }

    private static readonly Regex IdentifierWord = new(@"^[A-Za-z_][\w.]*$", RegexOptions.Compiled);

    private static int Find(JsonElement root, string[] terms, bool full)
    {
        List<TypeEntry> types = Types(root);
        string sourceRoot = Path.Combine(EnginePackage.Locate().EngineDir, "src");
        Console.WriteLine($"源码根 {sourceRoot}（下列路径相对于该源码根）");

        List<(string Label, string[] Terms, bool ByName)> queries = [.. terms.Where(term => IdentifierWord.IsMatch(term)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => (term, new[] { term }, true))];
        string[] prose = [.. terms.Where(term => !IdentifierWord.IsMatch(term))];
        if (prose.Length > 0) queries.Add((string.Join(" ", prose), prose, false));

        bool allHit = true;
        foreach ((string label, string[] words, bool byName) in queries)
        {
            if (queries.Count > 1) Console.WriteLine($"\n「{label}」");
            (HashSet<TypeEntry> typeHits, HashSet<MemberEntry> memberHits) = byName ? ByName(types, words[0]) : ([], []);
            bool fellBack = byName && typeHits.Count == 0 && memberHits.Count == 0;
            if (!byName || fellBack) (typeHits, memberHits) = ByText(types, words);
            if (typeHits.Count == 0 && memberHits.Count == 0)
            {
                allHit = false;
                Console.WriteLine($"「{label}」无匹配：作者面 {types.Count} 个类型与 {types.Sum(type => type.Members.Count)} 个成员，名字、签名与注释均无匹配"
                    + (words.Length > 1 ? "（多个关键词须同时匹配）" : ""));
                if (byName) PrintNearNames(types, label);
                continue;
            }

            if (fellBack) Console.WriteLine($"名字里没有「{label}」，以下按签名与注释全文命中");
            Print(types, typeHits, memberHits, words, full);
        }

        return allHit ? 0 : 1;
    }

    private static (HashSet<TypeEntry>, HashSet<MemberEntry>) ByName(List<TypeEntry> types, string word)
    {
        int dot = word.LastIndexOf('.');
        string owner = dot < 0 ? "" : word[..dot];
        string name = dot < 0 ? word : word[(dot + 1)..];
        static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

        HashSet<TypeEntry> typeHits = dot < 0 ? [.. types.Where(type => Has(type.Name, word))] : [];
        List<TypeEntry> owners = [.. types.Where(type => type.Name.Equals(owner, StringComparison.OrdinalIgnoreCase))];
        if (owners.Count == 0) owners = [.. types.Where(type => Has(type.Name, owner))];
        HashSet<MemberEntry> memberHits = [.. owners.SelectMany(type => type.Members).Where(member => Has(member.Name, name))];
        return (typeHits, memberHits);
    }

    private static (HashSet<TypeEntry>, HashSet<MemberEntry>) ByText(List<TypeEntry> types, string[] words)
    {
        bool Matches(string text) => words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        HashSet<TypeEntry> typeHits = [.. types.Where(type => Matches($"{type.Name} {DocText(type.Doc)}"))];
        HashSet<MemberEntry> memberHits = [.. types.SelectMany(type => type.Members)
            .Where(member => Matches($"{member.Owner.Name} {member.Signature} {DocText(member.Element)}"))];
        return (typeHits, memberHits);
    }

    private const int NearGroups = 4;
    private const int NearPerGroup = 8;
    private const int NearPrefix = 5;

    private static void PrintNearNames(List<TypeEntry> types, string query)
    {
        string[] wanted = CamelWords(query);
        if (wanted.Length == 0) return;

        (int Words, int Length) Score(string name)
        {
            string[] have = CamelWords(name);
            int[] best = [.. wanted.Select(word => have.Select(other => Affinity(word, other)).DefaultIfEmpty(0).Max()).Where(n => n > 0)];
            return (best.Length, best.Sum());
        }

        var near = new List<(string Owner, string Name, int Words, int Length)>();
        foreach (TypeEntry type in types)
        {
            (int words, int length) = Score(type.Name);
            if (words > 0) near.Add(("", type.Name, words, length));
            foreach (string member in type.Members.Select(member => member.Name).Where(name => name != type.Name).Distinct())
            {
                (words, length) = Score(member);
                if (words > 0) near.Add((type.Name, member, words, length));
            }
        }
        if (near.Count == 0) return;

        var groups = near.GroupBy(item => item.Owner)
            .OrderByDescending(group => group.Max(item => item.Words))
            .ThenByDescending(group => group.Max(item => item.Length))
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine("  相近的名字：");
        foreach (var group in groups.Take(NearGroups))
        {
            string[] names = [.. group.OrderByDescending(item => item.Words).ThenByDescending(item => item.Length)
                .ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name)];
            string rest = names.Length > NearPerGroup ? "，" + FindingPresentation.Omitted(names.Length - NearPerGroup, "个", null) : "";
            string head = group.Key == "" ? "类型" : group.Key;
            Console.WriteLine($"    {head}：{string.Join("、", names.Take(NearPerGroup))}{rest}");
        }
        if (groups.Count > NearGroups)
            Console.WriteLine("    " + FindingPresentation.Omitted(groups.Count - NearGroups, "个含相近名字的类型", null));
    }

    private static readonly Regex CamelWord = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", RegexOptions.Compiled);

    private static string[] CamelWords(string name)
    {
        string bare = name.StartsWith("RimRunner", StringComparison.Ordinal) ? name["RimRunner".Length..] : name;
        return [.. CamelWord.Matches(bare).Select(match => match.Value).Where(word => word.Length >= 3)];
    }

    private static int Affinity(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
        return n >= NearPrefix || n == Math.Min(a.Length, b.Length) ? n : 0;
    }

    private const int FlowShown = 6;

    private static void Print(List<TypeEntry> types, HashSet<TypeEntry> typeHits, HashSet<MemberEntry> memberHits, string[] words, bool full)
    {
        bool Mentions(string text) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

        var pulled = new Dictionary<MemberEntry, string>();
        foreach (MemberEntry hit in memberHits)
        {
            IEnumerable<MemberEntry> mates = hit.Owner.Kind == "enum"
                ? hit.Owner.Members
                : MethodName(hit.Element) is string method ? hit.Owner.Members.Where(member => MethodName(member.Element) == method) : [];
            string label = hit.Owner.Kind == "enum" ? "枚举值" : "重载";
            foreach (MemberEntry mate in mates)
                if (!memberHits.Contains(mate)) pulled.TryAdd(mate, label);
        }

        foreach (TypeEntry type in types)
        {
            bool typeHit = typeHits.Contains(type);
            bool listAll = typeHit && type.Kind == "enum";
            MemberEntry[] shown = [.. type.Members.Where(member => listAll || memberHits.Contains(member) || pulled.ContainsKey(member))];
            if (!typeHit && shown.Length == 0) continue;

            string kind = type.Kind == "" ? "" : $"{type.Kind} ";
            string elsewhere = type.OtherFiles > 0 ? $"（partial，另 {type.OtherFiles} 个文件）" : "";
            Console.WriteLine($"{kind}{type.Name}  {type.File}:{type.Line}{elsewhere} —— {SummaryOf(type.Doc)}"
                + (typeHit && !listAll ? $"  {type.Members.Count} 个成员（find {type.Name}. 全列）" : ""));
            if (typeHit) PrintDetail(type.Doc, "  ", full, Mentions);
            foreach (MemberEntry member in shown)
            {
                string mark = pulled.TryGetValue(member, out string? label) ? $"  同组：{label}" : "";
                Console.WriteLine($"    {member.Signature}  {member.File}:{member.Line} —— {SummaryOf(member.Element)}{mark}");
                PrintDetail(member.Element, "      ", full, Mentions);
            }
            if (typeHit || (type.Kind == "enum" && shown.Any(memberHits.Contains)))
                PrintFlow(types, type, full);
        }
    }

    private static void PrintFlow(List<TypeEntry> types, TypeEntry target, bool full)
    {
        var mention = new Regex($@"\b{Regex.Escape(target.Name)}\b");
        List<MemberEntry> takes = [];
        List<MemberEntry> gives = [];
        foreach (MemberEntry member in types.Where(type => type != target).SelectMany(type => type.Members))
        {
            string signature = member.Signature;
            int paren = signature.IndexOf('(');
            string produced = paren < 0
                ? signature[..Math.Max(0, signature.Length - member.Name.Length)]
                : signature[..MethodNamePattern.Match(signature[..paren]).Index];
            if (paren >= 0 && mention.IsMatch(signature[paren..])) takes.Add(member);
            if (mention.IsMatch(produced)) gives.Add(member);
        }

        foreach ((string label, List<MemberEntry> list) in new[] { ("接收该类型的成员", takes), ("产出该类型的成员", gives) })
        {
            IEnumerable<MemberEntry> printed = full ? list : list.Take(FlowShown);
            foreach (MemberEntry member in printed)
                Console.WriteLine($"  {label}：{member.Owner.Name}  {member.Signature}  {member.File}:{member.Line}");
            if (!full && list.Count > FlowShown)
                Console.WriteLine($"  {label}：" + FindingPresentation.Omitted(list.Count - FlowShown, "条", FindingPresentation.WithFlag("--full")));
        }
    }

    private static readonly Regex MethodNamePattern = new(@"(\w+)\s*(?:<[^()]*>)?\s*$", RegexOptions.Compiled);

    private static string? MethodName(JsonElement member)
    {
        string signature = member.GetProperty("signature").GetString() ?? "";
        int paren = signature.IndexOf('(');
        if (paren < 0 || ConstantValueAt(signature) is int assign && assign < paren) return null;
        Match name = MethodNamePattern.Match(signature[..paren]);
        return name.Success ? name.Groups[1].Value : null;
    }

    private static string LastWord(JsonElement member)
    {
        string signature = member.GetProperty("signature").GetString() ?? "";
        if (ConstantValueAt(signature) is int assign) signature = signature[..assign];
        return signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    }

    private static int? ConstantValueAt(string signature) =>
        signature.StartsWith("const ", StringComparison.Ordinal) || signature.Contains(" const ", StringComparison.Ordinal)
            ? signature.IndexOf(" = ", StringComparison.Ordinal) is int at and >= 0 ? at : null
            : null;

    private static void PrintDetail(JsonElement element, string indent, bool full, Func<string, bool> mentions)
    {
        List<string> notes = [];
        if (Text(element, "remarks") is string remarks) notes.Add($"范围：{remarks}");
        foreach (JsonElement param in Array(element, "params"))
            notes.Add($"参数 {param.GetProperty("name").GetString()}：{param.GetProperty("text").GetString()}");
        if (Text(element, "returns") is string returns) notes.Add($"返回：{returns}");
        foreach (JsonElement thrown in Array(element, "exceptions"))
            notes.Add($"抛 {thrown.GetProperty("cref").GetString()}：{thrown.GetProperty("text").GetString()}");

        List<string> printed = full ? notes : [.. notes.Where(mentions)];
        foreach (string note in printed) Console.WriteLine($"{indent}{note}");
        if (printed.Count < notes.Count)
            Console.WriteLine(indent + FindingPresentation.Omitted(notes.Count - printed.Count, "条（范围、参数、返回、异常）", FindingPresentation.WithFlag("--full")));
    }

    private static string DocText(JsonElement element)
    {
        List<string> parts = [SummaryOf(element)];
        foreach (string key in new[] { "remarks", "returns" })
            if (Text(element, key) is string text) parts.Add(text);
        foreach (JsonElement param in Array(element, "params"))
            parts.Add($"{param.GetProperty("name").GetString()} {param.GetProperty("text").GetString()}");
        foreach (JsonElement thrown in Array(element, "exceptions"))
            parts.Add($"{thrown.GetProperty("cref").GetString()} {thrown.GetProperty("text").GetString()}");
        return string.Join(" ", parts);
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string key) =>
        element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string SummaryOf(JsonElement element) => Text(element, "summary") ?? "（无摘要）";

    private static int Fixtures(JsonElement root, bool json)
    {
        JsonElement fixtures = root.GetProperty("fixtures");
        if (json)
        {
            CliLog.Machine(() => Console.WriteLine(fixtures.GetRawText()));
            return 0;
        }

        foreach (JsonElement unit in fixtures.EnumerateArray())
            Console.WriteLine($"{unit.GetProperty("name").GetString()} —— {SummaryOf(unit)}");
        return 0;
    }
}
