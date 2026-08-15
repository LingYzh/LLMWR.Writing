namespace LLMW.Writing.Contracts.Ipc;

/// <summary>
/// Endian-stable frame IO. Callers must not write header/body pairs concurrently on the same stream.
/// </summary>
public static class IpcFrameIO
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        IpcFrameHeader.ValidateLength(utf8Json.Length);
        var header = IpcFrameHeader.Create(utf8Json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(utf8Json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[IpcFrameHeader.Size];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!IpcFrameHeader.TryParse(header, out var payloadLength, out var errorCode))
        {
            throw new IpcFrameException(errorCode ?? IpcErrorCodes.InvalidFrame, "The IPC frame header is invalid.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC stream closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }
}

public sealed class IpcFrameException : Exception
{
    public IpcFrameException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
