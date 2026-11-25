using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace RimRunnerCli;

internal static class OrchestratorPipe
{
    internal const string LaunchVerb = "__orchestrator-launch";
    internal const string HostVerb = "__orchestrator";
    internal const int MaxFrameBytes = 16 * 1024 * 1024;

    internal static string Build => typeof(OrchestratorPipe).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    internal static string User
    {
        get
        {
            var name = new StringBuilder();
            foreach (char c in Environment.UserName)
                name.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            return name.ToString();
        }
    }

    internal static string Name() => Name(Build, AppContext.BaseDirectory);

    internal static string Name(string build, string packageRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(CommandContext.FullPath(packageRoot)).ToUpperInvariant();
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(build + "|" + root));
        return $"rimrunner-{User}-{Convert.ToHexStringLower(key.AsSpan(0, 16))}";
    }

    internal static string? MvidOnDisk(string dllPath) =>
        RimTooling.IO.PeImage.TryReadIdentity(CommandContext.FullPath(dllPath), out RimTooling.IO.PeIdentity identity)
            ? identity.Mvid.ToString("N")
            : null;

    internal static NamedPipeServerStream CreateServer(string name, bool first) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    internal static NamedPipeClientStream CreateClient(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
