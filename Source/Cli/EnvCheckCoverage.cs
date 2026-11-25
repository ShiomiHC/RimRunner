using System;

namespace RimRunnerCli;

public static class EnvCheckCoverage
{
    public static string ExcludeKey(StructuredRunRequest request) =>
        string.Join(",", request.ExcludeIds);

    internal static bool Covered(InstanceRuntime runtime, StructuredRunRequest request) =>
        runtime.EnvChecksGreenExcludes != null
        && string.Equals(runtime.EnvChecksGreenExcludes, ExcludeKey(request), StringComparison.Ordinal);

    internal static StructuredRunRequest ForReuse(InstanceRuntime runtime, StructuredRunRequest request,
        out bool envChecksStripped)
    {
        StructuredRunRequest reused = Covered(runtime, request) ? request.WithoutEnvChecks() : request;
        envChecksStripped = request.EnvChecks != reused.EnvChecks;
        return reused;
    }

    internal static InstanceRuntime Record(InstanceRuntime runtime, StructuredRunRequest request, EngineReport? report)
    {
        if (request.EnvChecks != StructuredRunRequest.EnvCheckMode.Off && report?.EnvChecksClean is bool clean)
            runtime = runtime with { EnvChecksGreenExcludes = clean ? ExcludeKey(request) : null };

        return runtime;
    }
}
