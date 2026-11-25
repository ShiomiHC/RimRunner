using System;
using System.IO;

namespace RimRunner;

internal enum PathPresence
{
    Present,
    Absent,

    Unknown,
}

internal static class RimRunnerPathPresence
{
    internal static PathPresence Of(string path)
    {
        PathPresence first = Query(path);
        if (first != PathPresence.Absent) return first;

        bool writing;
        try
        {
            writing = RimRunnerAtomicFile.HasTemporary(path);
        }
        catch (DirectoryNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (IOException)
        {
            return PathPresence.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return PathPresence.Unknown;
        }

        PathPresence again = Query(path);
        return again != PathPresence.Absent ? again : writing ? PathPresence.Unknown : PathPresence.Absent;
    }

    private static PathPresence Query(string path)
    {
        try
        {
            File.GetAttributes(path);
            return PathPresence.Present;
        }
        catch (FileNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (IOException)
        {
            return PathPresence.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return PathPresence.Unknown;
        }
    }
}
