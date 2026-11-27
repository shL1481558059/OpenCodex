using OpenCodex.Core.Errors;
using OpenCodex.ProtocolConversion.Errors;

namespace OpenCodex.Core.ProtocolConversion;

internal static class ProtocolConversionErrors
{
    public static void Wrap(Action action)
    {
        try
        {
            action();
        }
        catch (OpenCodex.ProtocolConversion.Errors.BadRequestException exception)
        {
            throw new OpenCodex.Core.Errors.BadRequestException(exception.Message, exception.StatusCode);
        }
        catch (OpenCodex.ProtocolConversion.Errors.UpstreamException exception)
        {
            throw new OpenCodex.Core.Errors.UpstreamException(
                exception.Message,
                exception.StatusCode,
                errorCode: exception.ErrorCode);
        }
    }

    public static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (OpenCodex.ProtocolConversion.Errors.BadRequestException exception)
        {
            throw new OpenCodex.Core.Errors.BadRequestException(exception.Message, exception.StatusCode);
        }
        catch (OpenCodex.ProtocolConversion.Errors.UpstreamException exception)
        {
            throw new OpenCodex.Core.Errors.UpstreamException(
                exception.Message,
                exception.StatusCode,
                errorCode: exception.ErrorCode);
        }
    }

    public static async IAsyncEnumerable<T> WrapStream<T>(
        IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            T item;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }
                item = enumerator.Current;
            }
            catch (OpenCodex.ProtocolConversion.Errors.BadRequestException exception)
            {
                throw new OpenCodex.Core.Errors.BadRequestException(exception.Message, exception.StatusCode);
            }
            catch (OpenCodex.ProtocolConversion.Errors.UpstreamException exception)
            {
                throw new OpenCodex.Core.Errors.UpstreamException(
                    exception.Message,
                    exception.StatusCode,
                    errorCode: exception.ErrorCode);
            }
            yield return item;
        }
    }
}
