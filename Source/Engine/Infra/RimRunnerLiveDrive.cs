using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private static LiveResponse DriveRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Drive.Kind, RimRunnerLiveContract.Drive.Verb, RimRunnerLiveContract.Drive.Args,
            RimRunnerLiveContract.Drive.LongEventSeconds, RimRunnerLiveContract.Drive.LimitSeconds, RimRunnerLiveContract.Drive.Assembly);
        string kind = p.Text(RimRunnerLiveContract.Drive.Kind) ?? RimRunnerLiveContract.Drive.KindDo;
        if (kind is not (RimRunnerLiveContract.Drive.KindDo or RimRunnerLiveContract.Drive.KindUntil))
        {
            return ErrorResponse(400, "kind 只接受 " + RimRunnerLiveContract.Drive.KindDo + " 或 " + RimRunnerLiveContract.Drive.KindUntil
                                      + "，收到：" + kind);
        }

        string name = p.RequireText(RimRunnerLiveContract.Drive.Verb, kind == RimRunnerLiveContract.Drive.KindUntil ? "条件名" : "原语名或脚本名");
        RimRunnerDriveVerb verb = RimRunnerDrive.FindVerb(kind, name);

        byte[]? image = p.Text(RimRunnerLiveContract.Drive.Assembly) is { } encoded ? DecodeAssembly(encoded) : null;
        bool scripted = kind == RimRunnerLiveContract.Drive.KindUntil ? name != RimRunnerDriveTable.UntilLetter : verb is RimRunnerDriveAuthor;
        if (scripted != (image != null))
        {
            return ErrorResponse(400, scripted
                ? "「" + name + "」不是内置" + (kind == RimRunnerLiveContract.Drive.KindUntil ? "条件" : "原语") + "，请求须带脚本的程序集（"
                  + RimRunnerLiveContract.Drive.Assembly + "）"
                : "「" + name + "」为内置" + (kind == RimRunnerLiveContract.Drive.KindUntil ? "条件" : "原语") + "，请求不得带程序集");
        }

        IReadOnlyDictionary<string, string> args =
            p.TextMap(RimRunnerLiveContract.Drive.Args) ?? new Dictionary<string, string>();
        string? argError = verb.CheckArgs(args);
        if (argError != null)
        {
            return ErrorResponse(400, argError);
        }

        int longEventSeconds = p.Int(RimRunnerLiveContract.Drive.LongEventSeconds) ?? RimRunnerLiveContract.Drive.DefaultLongEventSeconds;
        if (longEventSeconds < 0)
        {
            return ErrorResponse(400, RimRunnerLiveContract.Drive.LongEventSeconds + " 不能为负数，收到：" + longEventSeconds);
        }

        int limitSeconds = p.Int(RimRunnerLiveContract.Drive.LimitSeconds) ?? RimRunnerLiveContract.Drive.DefaultLimitSeconds;
        if (limitSeconds < 0)
        {
            return ErrorResponse(400, RimRunnerLiveContract.Drive.LimitSeconds + " 不能为负数，收到：" + limitSeconds);
        }

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return RimRunnerDrive.TryAccept(kind, name, verb, args, image, longEventSeconds, limitSeconds, out RimRunnerRunSnapshot busy, out string? statusJson) == null
            ? BusyResponse(busy)
            : JsonResponse(202, statusJson!);
    }

    private static LiveResponse DriveStatusRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Drive.Id);
        string id = p.RequireText(RimRunnerLiveContract.Drive.Id, "操作 id");
        string? json = RimRunnerDrive.StatusJson(id);
        return json == null ? ErrorResponse(404, MissingStatus(id)) : JsonResponse(200, json);
    }

    private static LiveResponse DriveCancelRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Drive.Id);
        string id = p.RequireText(RimRunnerLiveContract.Drive.Id, "操作 id");
        bool requested = RimRunnerDrive.RequestCancel(id);
        string? json = RimRunnerDrive.StatusJson(id);
        return json == null
            ? ErrorResponse(404, MissingStatus(id))
            : JsonResponse(requested ? 202 : 200, json);
    }

    private static string MissingStatus(string id) => RimRunnerDrive.StatusEvicted(id)
        ? "该操作的回执已被更新的操作覆盖：" + id
        : "没有该操作 id：" + id;

    internal static void WriteTextArray(Utf8JsonWriter writer, string key, IEnumerable<string> values)
    {
        writer.WriteStartArray(key);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
