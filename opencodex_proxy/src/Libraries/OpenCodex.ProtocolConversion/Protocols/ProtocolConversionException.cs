using OpenCodex.ProtocolConversion.Abstractions;

namespace OpenCodex.ProtocolConversion.Protocols;

public sealed class ProtocolConversionException : Exception, IProtocolConversionException
{
    public ProtocolConversionException(
        string message,
        ProtocolConversionErrorKind errorKind = ProtocolConversionErrorKind.InvalidRequest)
        : base(message)
    {
        ErrorKind = errorKind;
    }

    public ProtocolConversionErrorKind ErrorKind { get; }
}
