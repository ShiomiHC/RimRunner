namespace RimRunnerCli;

public static class KnownFailingPolicy
{
    public const string DirectiveKey = "known_failing";

    public sealed record Ack(string Id, string Reason);

    public static IReadOnlyList<Ack> Resolve(string? modListName, IReadOnlyList<string>? lines)
    {
        if (lines == null)
            return [];
        if (lines.Count == 0)
            throw new StoreException($"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 不能为空列表");
        return
        [
            .. lines.Select(line => AcknowledgePolicy.SplitArrow(modListName, DirectiveKey, "<体检Id> => <理由>", line))
                .Select(pair => new Ack(pair.Left, pair.Right)),
        ];
    }
}
