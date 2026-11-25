namespace RimRunner;

public static class RimRunnerCaseGroups
{
    public const string AllToken = "all";

    public static string? ShapeProblem(string group)
    {
        if (group.Length == 0)
            return "组名为空";
        if (string.Equals(group, AllToken, StringComparison.OrdinalIgnoreCase))
            return "组名 " + group + " 留作「不按组过滤」";
        foreach (char c in group)
        {
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return "组名 " + group + " 含英文字母、数字、- 与 _ 以外的字符";
        }

        return null;
    }

    public static string[] Normalize(IEnumerable<string>? groups)
    {
        return groups == null
            ? []
            : groups.Distinct(StringComparer.Ordinal).OrderBy(group => group, StringComparer.Ordinal).ToArray();
    }
}
