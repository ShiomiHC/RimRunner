namespace RimRunnerCli;

public static class AcknowledgePolicy
{
    public const string DirectiveKey = "acknowledge";

    private const string Arrow = " => ";

    public sealed record Ack(string Key, string Reason);

    public static IReadOnlyList<Ack> Resolve(string? modListName, IReadOnlyList<string>? lines)
    {
        if (lines == null)
            return [];
        if (lines.Count == 0)
            throw new StoreException($"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 不能为空列表");
        return
        [
            .. lines.Select(line => SplitArrow(modListName, DirectiveKey, "<键> => <理由>", line))
                .Select(pair => new Ack(pair.Left, pair.Right)),
        ];
    }

    internal static (string Left, string Right) SplitArrow(string? modListName, string directiveKey, string shape, string line)
    {
        string padded = line + " ";
        int cut = padded.LastIndexOf(Arrow, StringComparison.Ordinal);
        string left = cut < 0 ? "" : line[..cut].Trim();
        if (left.Length == 0)
        {
            throw new StoreException(
                $"mod 名单 {modListName} 的 [rimrunner].{directiveKey} 须写成「{shape}」，箭头前不得为空：{line}");
        }

        return (left, padded[(cut + Arrow.Length)..].Trim());
    }
}
