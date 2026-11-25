namespace RimRunner;

internal static class RimRunnerAssertLine
{
    public const string True = "true";
    public const string NoneOffending = "无违例";
    public const string AllTraversed = "全部被遍历";
    public const string RosterTraversed = "名册全部被遍历";

    public static bool IsFixed(string? expected) =>
        expected is True or NoneOffending or AllTraversed or RosterTraversed;

    public static string[] Parts(string label, string? expected, string actual) =>
        IsFixed(expected)
            ? [label, "：", actual]
            : [label, "：期望 ", expected ?? "", "，实际 ", actual];
}
