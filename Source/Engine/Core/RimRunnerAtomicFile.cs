namespace RimRunner;

internal static class RimRunnerAtomicFile
{
    internal static bool HasTemporary(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        string name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || name.Length == 0) return false;
        foreach (string candidate in System.IO.Directory.EnumerateFiles(directory, name + ".*.tmp"))
        {
            string candidateName = System.IO.Path.GetFileName(candidate);
            if (candidateName.Length != name.Length + 1 + 32 + 4
                || !candidateName.StartsWith(name + ".", System.StringComparison.OrdinalIgnoreCase)
                || !candidateName.EndsWith(".tmp", System.StringComparison.OrdinalIgnoreCase))
                continue;
            bool hex = true;
            for (int i = name.Length + 1; i < name.Length + 1 + 32; i++)
                hex &= System.Uri.IsHexDigit(candidateName[i]);
            if (hex) return true;
        }

        return false;
    }
}
