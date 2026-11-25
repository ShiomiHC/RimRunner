namespace RimRunnerCli;

public static class AcknowledgeStartupErrorPolicy
{
    public const string DirectiveKey = "acknowledge_startup_error";

    public sealed record Ack(string Pattern, string Reason);

    public static IReadOnlyList<Ack> Resolve(string? modListName, IReadOnlyList<string>? lines)
    {
        if (lines == null)
            return [];
        if (lines.Count == 0)
            throw new StoreException($"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 不能为空列表");
        return
        [
            .. lines.Select(line => AcknowledgePolicy.SplitArrow(modListName, DirectiveKey, "<子串> => <理由>", line))
                .Select(pair => new Ack(pair.Left, pair.Right)),
        ];
    }
}
