using System.ComponentModel;
using System.Text.Json;

namespace RimRunnerCli;

internal static class ExitPolicy
{
    internal static int FromException(Exception exception) => exception switch
    {
        GameUnavailableException => ExitCodes.GameUnavailable,
        RefusalException => ExitCodes.Rejected,
        StoreException
            or DescriptorException
            or ChannelClosedException
            or ReportFlowException
            or SampleOverlapException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or OperationCanceledException
            or JsonException
            or InvalidDataException
            or Win32Exception => ExitCodes.Incomplete,
        AggregateException aggregate when aggregate.Flatten().InnerExceptions is { Count: > 0 } inner =>
            Worst(inner.Select(FromException)),
        _ => ExitCodes.InternalFault,
    };

    internal static int Worst(params ReadOnlySpan<int> codes) => ExitCodes.Worst([.. codes]);

    internal static int Worst(IEnumerable<int> codes) => ExitCodes.Worst(codes);
}

public sealed class GameUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
