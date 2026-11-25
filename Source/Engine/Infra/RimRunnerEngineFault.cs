namespace RimRunner;

internal static class RimRunnerEngineFault
{
    internal const string LogMark = "[engine] 引擎故障 · ";

    internal static void Raise(string text)
    {
        text ??= "<null>";
        RimRunnerLogMonitor.RecordEngineFault(text, RimRunnerObservationBuffer.ActiveCaseId);
        RimRunnerEngineLog.Warn(LogMark + text);
    }

    internal static void Raise(string text, Exception exception)
    {
        Raise(text + "：" + exception);
    }
}
