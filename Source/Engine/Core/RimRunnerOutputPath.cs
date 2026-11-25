using System;
using System.IO;

namespace RimRunner;

internal static class RimRunnerOutputPath
{
    public const char Separator = '/';

    public static string Join(params string[] segments) => string.Join(Separator.ToString(), segments);

    public static string? Fault(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "路径为空";
        }

        foreach (string segment in path.Split(Separator))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return "路径含空段、. 段或 .. 段：" + path;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "路径段含文件名非法字符：" + path;
            }
        }

        return null;
    }
}
