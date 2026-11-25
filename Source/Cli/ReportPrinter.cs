namespace RimRunnerCli;

public static class ReportPrinter
{
    public static void Write(IEnumerable<ReportLine> lines, Action<string> screen)
    {
        foreach (ReportLine line in lines)
        {
            if (line.Screen == null)
            {
                CliLog.Info(line.Log);
                CliLog.PointAtLog();
                continue;
            }

            string shown = FindingPresentation.CapScreenLine(line.Screen);
            if (shown == line.Log)
                screen(shown);
            else
                CliLog.ScreenAs(shown, line.Log, screen);
        }
    }
}
