using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private static LiveResponse ProfilerRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly("action", "prefix");
        string? action = p.Text("action");
        if (string.IsNullOrEmpty(action))
        {
            return ErrorResponse(400,
                "需要 action 参数：arm（安装探针并启用）、status（查看探针是否已安装）、"
                + "reset（清空累积序列）、dump（导出逐周期原始序列，可带 prefix 过滤 key）");
        }

        switch (action)
        {
            case "arm":
            {
                string? failure = RimRunnerProfilerBridge.Arm();
                if (failure != null) return ErrorResponse(503, failure);
                return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Profiler, writer =>
                {
                    writer.WriteBoolean(RimRunnerLiveContract.Profiler.Armed, true);
                }));
            }

            case "arm-compare":
                return ArmCompareRequest();

            case "status":
                return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Profiler, writer =>
                {
                    writer.WriteBoolean(RimRunnerLiveContract.Profiler.Armed, RimRunnerProfilerBridge.Armed);
                }));

            case "reset":
            {
                string? failure = RimRunnerProfilerBridge.Reset();
                if (failure != null) return ErrorResponse(503, failure);
                return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Profiler, writer =>
                    writer.WriteBoolean(RimRunnerLiveContract.Profiler.Reset, true)));
            }

            case "dump":
            {
                string? refusal = RimRunnerProfilerBridge.ArmedRefusalOrNull();
                if (refusal != null) return ErrorResponse(503, refusal);
                string? prefix = p.Text("prefix");
                return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Profiler,
                    writer => RimRunnerProfilerBridge.AppendDump(writer, prefix)));
            }

            default:
                return ErrorResponse(400, "未知 action：" + action);
        }
    }

    private static LiveResponse ArmCompareRequest()
    {
        if (!RimRunnerProfilerBridge.Armed)
        {
            return ErrorResponse(409, "无法添加对照组：profiler 未启用");
        }

        Map? map = RimRunnerIsolatedMap.LiveMap;
        if (map == null) return ErrorResponse(503, "没有当前地图，无法收集对照组");

        List<(string Name, string? Failure, int MethodCount)> groups =
            [];

        string? tickFailure = RimRunnerProfilerBridge.ArmExtraGroupLive(
            "TickRoot", RimRunnerProfilerTargets.CollectTickRoot());
        groups.Add(("TickRoot", tickFailure, 1));

        foreach (KeyValuePair<string, HashSet<MethodInfo>> pair in
                 RimRunnerProfilerTargets.CollectPawnCompGroups(map))
        {
            string? failure = RimRunnerProfilerBridge.ArmExtraGroupLive(pair.Key, pair.Value);
            groups.Add((pair.Key, failure, pair.Value.Count));
        }

        foreach (KeyValuePair<string, HashSet<MethodInfo>> pair in
                 RimRunnerProfilerTargets.CollectGlobalGroups(map))
        {
            string? failure = RimRunnerProfilerBridge.ArmExtraGroupLive(pair.Key, pair.Value);
            groups.Add((pair.Key, failure, pair.Value.Count));
        }

        int armedGroups = groups.Count(group => group.Failure == null);
        return JsonResponse(202, InjectSchema(RimRunnerLiveContract.Schema.Profiler, writer =>
        {
            writer.WriteNumber(RimRunnerLiveContract.Profiler.ArmedGroups, armedGroups);
            writer.WriteStartArray(RimRunnerLiveContract.Profiler.Groups);
            foreach ((string Name, string? Failure, int MethodCount) group in groups)
            {
                AppendGroupResult(writer, group.Name, group.Failure, group.MethodCount);
            }

            writer.WriteEndArray();
        }));
    }

    private static void AppendGroupResult(Utf8JsonWriter writer, string name,
        string? failure, int methodCount)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteNumber("methods", methodCount);
        writer.WriteBoolean("ok", failure == null);
        if (failure != null)
        {
            writer.WriteString("error", failure);
        }

        writer.WriteEndObject();
    }
}
