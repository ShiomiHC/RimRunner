using System.IO;

namespace RimRunner;

internal static class RimRunnerShowcaseReadMarks
{
    private static HashSet<string>? marks;

    internal static bool IsRead(string id)
    {
        return Marks.Contains(id);
    }

    internal static void Toggle(string id)
    {
        if (!Marks.Remove(id))
        {
            Marks.Add(id);
        }

        Save();
    }

    internal static void Clear()
    {
        Marks.Clear();
        Save();
    }

    private static HashSet<string> Marks
    {
        get
        {
            if (marks == null)
            {
                string path = RimRunnerPaths.ShowcaseReadMarksFile;
                marks = new HashSet<string>(
                    RimRunnerPathPresence.Of(path) == PathPresence.Absent
                        ? []
                        : File.ReadAllLines(path).Where(line => line.Length > 0),
                    StringComparer.Ordinal);
            }

            return marks;
        }
    }

    private static void Save()
    {
        RimRunnerOutput.Replace(RimRunnerPaths.ShowcaseReadMarksPath, System.Text.Encoding.UTF8.GetBytes(
                string.Concat(marks!.OrderBy(id => id, StringComparer.Ordinal).Select(id => id + Environment.NewLine))),
            quiet: true);
    }
}
