namespace RimRunnerCli;

public static class ComponentExemptionPolicy
{
    public const string DirectiveKey = "component_exemption";

    private const string Shape = "<组件类型全名>:<元素>[,<元素>…] => <理由>";

    public sealed record Exemption(string Target, string Reason);

    public static IReadOnlyList<Exemption> Resolve(string? modListName, IReadOnlyList<string>? lines)
    {
        if (lines == null)
            return [];
        if (lines.Count == 0)
            throw new StoreException($"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 不能为空列表");

        List<Exemption> result = [];
        foreach (string line in lines)
        {
            (string target, string reason) = AcknowledgePolicy.SplitArrow(modListName, DirectiveKey, Shape, line);
            int colon = target.IndexOf(':');
            bool shaped = colon > 0
                          && target[(colon + 1)..].Split(',').All(element => element.Trim().Length > 0)
                          && !target[..colon].Any(char.IsWhiteSpace);
            if (!shaped)
                throw new StoreException(
                    $"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 须写成「{Shape}」，类型名不得含空白、元素名不得为空：{line}");
            result.Add(new Exemption(target, reason));
        }

        return result;
    }
}
