namespace RimRunnerCli;

public static class ModsConfigCaseGate
{
    public static string? Verdict(IReadOnlyList<string> activeIds)
    {
        string[] upper =
        [
            .. activeIds
                .Where(id => id.Any(char.IsUpper))
        ];
        if (upper.Length == 0)
        {
            return null;
        }

        return $"生效 mod 名单里有 {upper.Length} 个 packageId 含大写：{string.Join("、", upper)}"
            + Environment.NewLine
            + "  游戏视它们为未启用：按这些 packageId 门控的内容（MayRequire / IfModActive）不载入";
    }

    public static void Assert(IReadOnlyList<string> activeIds)
    {
        string? complaint = Verdict(activeIds);
        if (complaint != null)
        {
            throw new StoreException(complaint);
        }
    }
}
