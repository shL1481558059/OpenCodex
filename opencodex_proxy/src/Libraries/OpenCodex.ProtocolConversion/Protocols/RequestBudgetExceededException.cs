using OpenCodex.ProtocolConversion.Abstractions;

namespace OpenCodex.ProtocolConversion.Protocols;

/// <summary>A complete logical or encoded request exceeds the root request budget.</summary>
public sealed class RequestBudgetExceededException : Exception, IProtocolConversionException
{
    public RequestBudgetExceededException(
        long maximumBytes,
        long? actualBytes = null,
        Exception? innerException = null)
        : base("request body too large", innerException)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (actualBytes is < 0) throw new ArgumentOutOfRangeException(nameof(actualBytes));
        MaximumBytes = maximumBytes;
        ActualBytes = actualBytes;
    }

    public long MaximumBytes { get; }
    // A bounded encoder may stop at the first over-budget write. In that case this
    // is the observed lower bound, not a claim that the remaining JSON was encoded.
    public long? ActualBytes { get; }
    public int StatusCode => 413;
    public ProtocolConversionErrorKind ErrorKind => ProtocolConversionErrorKind.InvalidRequest;
}
