using System.Runtime.CompilerServices;
using System.Text.Json;
using LudeonTK;

namespace RimRunner;

internal static class RimRunnerDevMenu
{
    internal const char Separator = '\\';

    internal const int FindLimit = 50;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "childrenSetup")]
    private static extern ref bool ChildrenSetup(DebugActionNode node);

    internal static DebugActionNode Root
    {
        get
        {
            Dialog_Debug.TrySetupNodeGraph();
            return Dialog_Debug.rootNode;
        }
    }

    internal static bool Unexpanded(DebugActionNode node) => node.childGetter != null && !ChildrenSetup(node);

    internal static bool IsMenu(DebugActionNode node) => Unexpanded(node) || node.children.Count > 0;

    internal static string? Resolve(string path, out DebugActionNode node)
    {
        node = Root;
        if (path.Length == 0)
        {
            return null;
        }

        string walked = "";
        foreach (string label in path.Split(Separator))
        {
            if (Unexpanded(node))
            {
                return walked + " 是未展开的生成子菜单";
            }

            DebugActionNode[] matched = [.. node.children.Where(child => child.label == label)];
            string here = walked.Length == 0 ? label : walked + Separator + label;
            if (matched.Length == 0)
            {
                return (walked.Length == 0 ? "dev 菜单根下" : walked + " 下") + "没有「" + label + "」；有："
                       + string.Join("、", node.children.Select(child => child.label).Take(FindLimit))
                       + (node.children.Count > FindLimit ? "；其余 " + (node.children.Count - FindLimit) + " 项未列（总项数 " + node.children.Count + "）" : "");
            }

            if (matched.Length > 1)
            {
                return here + " 对应 " + matched.Length + " 个同名节点，原版只取第一个，本命令不代为选择";
            }

            node = matched[0];
            walked = here;
        }

        return null;
    }

    internal static string PathOf(DebugActionNode node) => node.IsRoot ? "" : node.Path;

    internal static bool? Setting(DebugActionNode node) =>
        node.settingsField is { } field && field.FieldType == typeof(bool) ? (bool)field.GetValue(null)! : null;

    internal static string Kind(DebugActionNode node)
    {
        if (Unexpanded(node))
        {
            return "生成子菜单，未展开";
        }

        if (node.children.Count > 0)
        {
            return "子菜单，" + node.children.Count + " 项";
        }

        if (Setting(node) is { } on)
        {
            return "设置，" + (on ? "开" : "关");
        }

        return node.actionType switch
        {
            DebugActionType.ToolMap => "地图工具",
            DebugActionType.ToolMapForPawns => "对 pawn 的地图工具",
            DebugActionType.ToolWorld => "世界工具",
            _ => "动作",
        };
    }

    internal static IReadOnlyList<DebugActionNode> Children(DebugActionNode node)
    {
        node.TrySort();
        return node.children;
    }

    internal static List<DebugActionNode> Find(string text)
    {
        var found = new List<DebugActionNode>();
        var pending = new Stack<DebugActionNode>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            DebugActionNode node = pending.Pop();
            if (!node.IsRoot && node.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(node);
            }

            if (Unexpanded(node))
            {
                continue;
            }

            for (int index = node.children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.children[index]);
            }
        }

        return found;
    }

    internal static void WriteItems(Utf8JsonWriter writer, IEnumerable<DebugActionNode> nodes)
    {
        writer.WriteStartArray(RimRunnerLiveContract.Dev.Items);
        foreach (DebugActionNode node in nodes)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerLiveContract.Dev.Path, node.Path);
            writer.WriteString(RimRunnerLiveContract.Dev.Kind, Kind(node));
            writer.WriteBoolean(RimRunnerLiveContract.Dev.Hidden, !node.VisibleNow);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

public static partial class RimRunnerLiveServer
{
    private static LiveResponse QueryDevRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Dev.Path, RimRunnerLiveContract.Dev.Find);
        string path = p.Text(RimRunnerLiveContract.Dev.Path) ?? "";
        string? find = p.Text(RimRunnerLiveContract.Dev.Find);
        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(() => find != null ? QueryDevFind(find) : QueryDevChildren(path));
    }

    private static LiveResponse QueryDevChildren(string path)
    {
        if (RimRunnerDevMenu.Resolve(path, out DebugActionNode node) is { } missing)
        {
            return ErrorResponse(404, missing);
        }

        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Dev, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Dev.Path, RimRunnerDevMenu.PathOf(node));
            writer.WriteString(RimRunnerLiveContract.Dev.Kind, node.IsRoot ? "根" : RimRunnerDevMenu.Kind(node));
            RimRunnerDevMenu.WriteItems(writer, RimRunnerDevMenu.Unexpanded(node) ? [] : RimRunnerDevMenu.Children(node));
        }));
    }

    private static LiveResponse QueryDevFind(string text)
    {
        List<DebugActionNode> found = RimRunnerDevMenu.Find(text);
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Dev, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Dev.Find, text);
            writer.WriteNumber(RimRunnerLiveContract.Dev.Total, found.Count);
            RimRunnerDevMenu.WriteItems(writer, found.Take(RimRunnerDevMenu.FindLimit));
        }));
    }
}
