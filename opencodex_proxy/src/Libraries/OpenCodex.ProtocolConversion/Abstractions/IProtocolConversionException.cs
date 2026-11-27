namespace OpenCodex.ProtocolConversion.Abstractions;

public interface IProtocolConversionException
{
    ProtocolConversionErrorKind ErrorKind { get; }
}

public enum ProtocolConversionErrorKind
{
    InvalidRequest,
    InvalidUpstreamResponse,
    UnsupportedConversion
}
