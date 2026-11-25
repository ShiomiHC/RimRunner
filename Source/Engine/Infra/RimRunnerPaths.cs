using System.IO;

namespace RimRunner;

internal static class RimRunnerPaths
{
    private static string RequiredArg(string name, string meaning)
    {
        if (GenCommandLine.TryGetCommandLineArg(name, out string value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        RimRunnerEngineFault.Raise("缺少 -" + name + "=<" + meaning + ">"
                  + "（由编排进程传入；手动以 -" + RimRunnerEngineArgs.Enable + " 启动时也须显式给出）");
        throw new InvalidOperationException(name + " missing on command line");
    }

    private static string RootDir => field ??= RequiredArg(RimRunnerEngineArgs.DataRoot, "测试数据根目录");

    private static string OutputDir => field ??= RequiredArg(RimRunnerEngineArgs.OutputRoot, "产出根目录");

    internal static string DataRoot => RootDir;

    internal static string OutputRoot => OutputDir;

    internal static readonly string CaseManifestPath =
        RimRunnerOutputPath.Join(RimRunnerDataLayout.IndexDirName, RimRunnerDataLayout.CaseManifestFileName);

    internal static string CaseManifestFile => RimRunnerOutput.Absolute(CaseManifestPath);

    internal const string ShowcaseReadMarksPath = "showcase-read.txt";

    internal static string ShowcaseReadMarksFile => RimRunnerOutput.Absolute(ShowcaseReadMarksPath);

    internal const string ReportsPath = RimRunnerDataLayout.ReportsDirName;

    internal const string HistoryPath = RimRunnerDataLayout.HistoryDirName;

    internal static readonly string ScreenshotsPath =
        RimRunnerOutputPath.Join(RimRunnerDataLayout.ArtifactsDirName, RimRunnerDataLayout.ArtifactsScreenshotsDirName);

    internal static readonly string ReadoutsPath =
        RimRunnerOutputPath.Join(RimRunnerDataLayout.ArtifactsDirName, RimRunnerDataLayout.ArtifactsReadoutsDirName);

    internal static IReadOnlyList<RimRunnerBorrowLayer> BorrowLayers => borrowLayers ??= ReadBorrowLayers();

    private static List<RimRunnerBorrowLayer>? borrowLayers;

    private static List<RimRunnerBorrowLayer> ReadBorrowLayers()
    {
        List<RimRunnerBorrowLayer> layers = [];
        AddLayer(layers, RimRunnerBorrowLayer.Main, RimRunnerEngineArgs.MainDataRoot, RimRunnerEngineArgs.MainOutputRoot);
        return layers;
    }

    private static void AddLayer(List<RimRunnerBorrowLayer> layers, string name, string dataArg, string outputArg)
    {
        if (GenCommandLine.TryGetCommandLineArg(dataArg, out string data) && !string.IsNullOrWhiteSpace(data)
            && GenCommandLine.TryGetCommandLineArg(outputArg, out string output) && !string.IsNullOrWhiteSpace(output))
        {
            layers.Add(new RimRunnerBorrowLayer(name, data, output));
        }
    }
}
