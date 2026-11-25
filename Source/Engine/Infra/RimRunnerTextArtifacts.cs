using System.Globalization;
using System.IO;
using System.Text;

namespace RimRunner;

internal static class RimRunnerTextArtifacts
{
    internal static int CountLines(string text)
    {
        string body = text.TrimEnd('\r', '\n');
        if (body.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        foreach (char character in body)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    internal static string Write(string dir, string caseId, int sequence, string text, out long bytes)
    {
        string runId = RimRunnerSession.ActiveRunId ?? "norun";
        string path = RimRunnerOutputPath.Join(dir,
            runId[..Math.Min(8, runId.Length)] + "-" + Sanitize(caseId) + "-"
            + sequence.ToString(CultureInfo.InvariantCulture) + ".txt");
        byte[] content = Encoding.UTF8.GetBytes(text);
        RimRunnerOutput.Replace(path, content);
        bytes = content.LongLength;
        return RimRunnerOutput.Absolute(path);
    }

    private static string Sanitize(string? caseId)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string value = new string((caseId ?? "case")
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray()).Trim('-');
        return value.Length == 0 ? "case" : value;
    }
}
