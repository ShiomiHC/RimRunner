namespace RimRunnerCli;

internal sealed class ProgressLine(Action<string>? write = null)
{
    private readonly Action<string> write = write ?? Console.Write;

    public void Begin(string text) => write(text);

    public void End(string suffix) => write(suffix + Environment.NewLine);
}
