using System.Text.Json;

namespace RimRunnerCli;

internal static class CoordinatorElection
{
    internal const string TakeVariable = "RIMRUNNER_TAKE_COORDINATOR";

    internal sealed record Candidate(string Pipe, string Build, string PackageRoot, string? DevTree, DateTimeOffset? BuiltAt)
    {
        internal bool Dev => DevTree != null;

        internal static Candidate Self()
        {
            PackageManifest? manifest = null;
            try
            {
                manifest = EnginePackage.Locate(_ => null).ReadManifest();
            }
            catch (StoreException)
            {
            }

            return new Candidate(OrchestratorPipe.Name(), OrchestratorPipe.Build,
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), manifest?.DevTree, manifest?.BuiltAt);
        }

        internal static Candidate Read(JsonElement element) =>
            new(element.GetProperty("pipe").GetString()!, element.GetProperty("build").GetString()!,
                element.GetProperty("packageRoot").GetString()!,
                element.TryGetProperty("devTree", out JsonElement tree) && tree.ValueKind == JsonValueKind.String
                    ? tree.GetString()
                    : null,
                element.TryGetProperty("builtAt", out JsonElement at) && at.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(at.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : null);

        internal void WriteFields(Utf8JsonWriter writer)
        {
            writer.WriteString("pipe", Pipe);
            writer.WriteString("build", Build);
            writer.WriteString("packageRoot", PackageRoot);
            if (DevTree != null) writer.WriteString("devTree", DevTree);
            if (BuiltAt is { } at) writer.WriteString("builtAt", at.ToString("O"));
        }

        internal void Write(Utf8JsonWriter writer, bool taken, string? logFile = null)
        {
            writer.WriteStartObject();
            WriteFields(writer);
            if (taken) writer.WriteBoolean("taken", true);
            if (logFile != null) writer.WriteString("logFile", logFile);
            writer.WriteEndObject();
        }
    }

    internal static bool Outranks(Candidate a, Candidate b)
    {
        if (a.Dev != b.Dev) return !a.Dev;
        return (a.BuiltAt ?? DateTimeOffset.MinValue) > (b.BuiltAt ?? DateTimeOffset.MinValue);
    }

    internal static bool ShouldYield(Candidate incumbent, bool taken, Candidate newcomer, bool take)
    {
        if (string.Equals(incumbent.Build, newcomer.Build, StringComparison.OrdinalIgnoreCase)
            && string.Equals(incumbent.PackageRoot, newcomer.PackageRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (take && newcomer.Dev) return true;
        if (taken) return false;
        return Outranks(newcomer, incumbent);
    }

    internal static string Describe(Candidate candidate) =>
        (candidate.Dev ? $"开发构建 {candidate.DevTree}" : $"RimRunner 安装目录 {candidate.PackageRoot}")
        + $"（构建 {candidate.Build[..Math.Min(8, candidate.Build.Length)]}"
        + (candidate.BuiltAt is { } at ? $"，编译于 {at:yyyy-MM-dd HH:mm:ss}" : "") + "）";

    internal static string Why(Candidate incumbent, Candidate newcomer, bool take)
    {
        if (take && newcomer.Dev) return $"接任方的命令带 {TakeVariable}=1";
        if (incumbent.Dev && !newcomer.Dev) return "接任方不是开发构建";
        return "接任方的构建较新";
    }

    internal static bool WantsTake(Func<string, string?> getEnvironmentVariable, Candidate self) =>
        self.Dev && getEnvironmentVariable(TakeVariable)?.Trim() == "1";
}
