using System.Text.Json;

namespace RimRunner;

internal static class RimRunnerEngineLog
{
    internal const string WarnMark = "[WARN] ";

    [ThreadStatic] private static List<string>? selfTestSink;

    internal static void Info(string text) => Write(text, warn: false, RimRunnerLog.Prefix + " ");

    internal static void Warn(string text) => Write(text, warn: true, RimRunnerLog.Prefix + " ");

    internal static void InfoTagged(string text) => Write(text, warn: false, "");

    internal static bool MarksOn { get; } = GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.ObservationMarks);

    internal static void Mark(string key, string text)
    {
        if (!MarksOn) return;
        string line = RimRunnerObservationKeys.Line(key, text);
        if (selfTestSink != null)
        {
            selfTestSink.Add(line);
            return;
        }

        Write(line, warn: false, RimRunnerLog.Prefix + " ");
    }

    internal static IDisposable CaptureForSelfTest(List<string> sink)
    {
        if (selfTestSink != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 引擎日志自测收集不得嵌套");
        }

        selfTestSink = sink ?? throw new ArgumentNullException(nameof(sink));
        return new SelfTestCapture();
    }

    private sealed class SelfTestCapture : IDisposable
    {
        public void Dispose()
        {
            selfTestSink = null;
        }
    }

    private static void Write(string text, bool warn, string fallbackPrefix)
    {
        text ??= "<null>";
        selfTestSink?.Add((warn ? WarnMark : "") + text);
        if (RimRunnerPipeLink.Connected)
        {
            DateTimeOffset at = DateTimeOffset.Now;
            RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Log, writer =>
            {
                writer.WriteString(RimRunnerPipeProtocol.Keys.At, at);
                writer.WriteString(RimRunnerPipeProtocol.Keys.Level,
                    warn ? RimRunnerPipeProtocol.Keys.LevelWarn : RimRunnerPipeProtocol.Keys.LevelInfo);
                writer.WriteString(RimRunnerPipeProtocol.Keys.Text, text);
            });
            return;
        }

        string line = fallbackPrefix + text;
        if (warn)
        {
            RimRunnerLog.Category.Warning(line);
        }
        else
        {
            RimRunnerLog.Category.Info(line);
        }
    }
}
