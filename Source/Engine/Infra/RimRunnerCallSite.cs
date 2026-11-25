using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerCallSite
{
    internal static MethodBase? MethodOf(StackFrame frame)
    {
        MethodBase? method = frame.GetMethod();
        return method is MethodInfo info ? Harmony.GetOriginalMethod(info) : method;
    }

    internal static string? Describe(StackTrace trace, int maxFrames, Func<Type, bool> skip)
    {
        var frames = new List<string>();
        for (int i = 0; i < trace.FrameCount && frames.Count < maxFrames; i++)
        {
            StackFrame? frame = trace.GetFrame(i);
            MethodBase? method = frame == null ? null : MethodOf(frame);
            if (method?.DeclaringType is not { } type || skip(type))
            {
                continue;
            }

            while (type.DeclaringType != null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                type = type.DeclaringType;
            }

            frames.Add(type.Name + "." + method.Name);
        }

        return frames.Count == 0 ? null : string.Join("←", frames);
    }
}
