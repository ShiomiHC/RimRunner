using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private const int SaveWaitMilliseconds = 120000;

    private static LiveResponse SaveRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly("name");
        string saveName = p.RequireText("name", "存档名，不含扩展名与路径");

        string? rejection = RejectUnsafeSaveName(saveName);
        if (rejection != null)
        {
            return ErrorResponse(400, rejection);
        }

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态，没有可存的世界");
        }

        string command = "save:" + saveName;
        if (!RimRunnerRunCoordinator.TryReserveSave(command, out RimRunnerRunSnapshot activeRun))
        {
            return BusyResponse(activeRun);
        }

        bool neverRan = false;
        return ExecuteOnMainThread(
            () =>
            {
                try
                {
                    return SaveGameAs(saveName);
                }
                finally
                {
                    RimRunnerRunCoordinator.Complete(command, null);
                }
            },
            allowBusy: true,
            waitMilliseconds: SaveWaitMilliseconds,
            timeoutResponse: () => ErrorResponse(504, neverRan
                ? "存档超时（" + SaveWaitMilliseconds / 1000 + "s）：主线程未轮到，未写入存档文件"
                : "存档超时（" + SaveWaitMilliseconds / 1000 + "s）：写入存档文件已开始，尚未确认结束；"
                  + "回到空闲前重试会被拒绝"),
            onNeverRun: () =>
            {
                neverRan = true;
                RimRunnerRunCoordinator.Complete(command, null);
            });
    }

    private static string? RejectUnsafeSaveName(string saveName)
    {
        if (saveName.IndexOf('/') >= 0 || saveName.IndexOf('\\') >= 0
                                       || saveName.Contains("..") || Path.IsPathRooted(saveName))
        {
            return "name 须是纯文件名：不得含路径分隔符、`..` 或盘符；收到：" + saveName;
        }

        if (saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "name 含文件名非法字符；收到：" + saveName;
        }

        return null;
    }

    private static LiveResponse SaveGameAs(string saveName)
    {
        string path = GenFilePaths.FilePathForSavedGame(saveName);
        bool overwrote = File.Exists(path);
        if (overwrote)
        {
            File.Delete(path);
        }

        RimRunnerDanglingRefProbe.Arm();
        GameDataSaveLoader.SaveGame(saveName);
        RimRunnerDanglingRefProbe.DisarmAndLog();
        if (!File.Exists(path))
        {
            return ErrorResponse(500, "SaveGame 调用返回后存档文件仍不存在：存档名=" + saveName + " 路径=" + path);
        }

        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Save, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Save.Saved, saveName);
            writer.WriteString(RimRunnerLiveContract.Save.Path, path);
            writer.WriteString(RimRunnerLiveContract.Save.Bytes,
                new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
            writer.WriteBoolean(RimRunnerLiveContract.Save.Overwrote, overwrote);
        }));
    }
}
