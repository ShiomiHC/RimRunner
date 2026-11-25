namespace RimRunner;

internal static class RimRunnerAssemblyRedirects
{
    internal const string Key = "WaylithClr.AssemblyNameRedirects";

    internal readonly record struct Rule(string Pattern, string Target)
    {
        internal bool Matches(string name) => Pattern.EndsWith('*')
            ? name.StartsWith(Pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, Pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<Rule> Active => field ??= Parse(AppContext.GetData(Key)?.ToString());

    internal static IReadOnlyList<Rule> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
                .Select(parts => new Rule(parts[0].Trim(), parts[1].Trim()))
                .ToList();

    internal static string? TargetOf(IReadOnlyList<Rule> rules, string name)
    {
        foreach (Rule rule in rules)
        {
            if (rule.Matches(name))
            {
                return rule.Target.Length > 0 ? rule.Target : null;
            }
        }

        return null;
    }
}
