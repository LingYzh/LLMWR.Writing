using System.Threading.Channels;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// In-process duplex streams with bounded segment queues. Production IPC uses Named Pipes.
/// </summary>
public static class IpcConnectedStreamPair
{
    public static (Stream Left, Stream Right) Create(int segmentCapacity = 32)
    {
        var leftToRight = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(segmentCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        var rightToLeft = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(segmentCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        return (
            new ChannelByteStream(rightToLeft.Reader, leftToRight.Writer),
            new ChannelByteStream(leftToRight.Reader, rightToLeft.Writer));
    }

    private sealed class ChannelByteStream : Stream
    {
        private readonly ChannelReader<byte[]> reader;
        private readonly ChannelWriter<byte[]> writer;
        private ReadOnlyMemory<byte> leftover;
        private int disposed;

        public ChannelByteStream(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer)
        {
            this.reader = reader;
            this.writer = writer;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (leftover.Length == 0)
            {
                byte[]? segment;
                try
                {
                    if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return 0;
                    }

                    if (!reader.TryRead(out segment))
                    {
                        return 0;
                    }
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                leftover = segment;
            }

            var copy = Math.Min(buffer.Length, leftover.Length);
            leftover.Span[..copy].CopyTo(buffer.Span);
            leftover = leftover[copy..];
            return copy;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            var copy = buffer.ToArray();
            await writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
