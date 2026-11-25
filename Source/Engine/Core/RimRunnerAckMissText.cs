namespace RimRunner;

internal static class RimRunnerAckMissText
{
    internal static string KeyLabel(string key, string? origin)
    {
        return "认领 " + (origin == null ? key : key + "[" + origin + "]");
    }

    internal static string LogLabel(string level, string pattern, System.Collections.Generic.IReadOnlyList<string> stackAnchors)
    {
        return (level == "warning" ? "警告级日志许可" : "错误级日志许可") + " \"" + pattern + "\""
               + (stackAnchors.Count > 0 ? "（栈锚 " + string.Join(" + ", stackAnchors) + "）" : "");
    }

    internal static string NoHit(string caseId, string label, int misses, int tolerance, string reason)
    {
        return "用例 " + caseId + " 的" + label + " 在执行到尾的轮里连续 " + misses + " 轮未命中，超过 Tolerance " + tolerance + "。认领理由：" + reason;
    }

    internal static string ReachHit(string caseId, string label, int hits, string unreachable)
    {
        return "用例 " + caseId + " 本轮声明够不到的" + label + " 仍命中 " + hits + " 次（声明的理由：" + unreachable
               + "），命中仍计入";
    }
}
