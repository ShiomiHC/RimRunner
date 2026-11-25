namespace RimRunner;

public sealed class RimRunnerClaimedFault : Exception
{
    public enum FaultKind
    {
        Failure,
        Precondition
    }

    public FaultKind Kind { get; }

    internal RimRunnerClaimedFault(FaultKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    internal static RimRunnerClaimedFault? Unwrap(Exception? exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is RimRunnerClaimedFault fault)
            {
                return fault;
            }
        }

        return null;
    }
}
