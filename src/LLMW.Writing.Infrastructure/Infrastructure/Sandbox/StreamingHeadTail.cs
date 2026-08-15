using System.Text;
using LLMW.Writing.Application.Security.Sandbox;

namespace LLMW.Writing.Infrastructure.Sandbox;

internal sealed class StreamingHeadTail
{
    private readonly byte[] head = new byte[SandboxPathPolicy.OutputHeadBytes];
    private readonly byte[] tail = new byte[SandboxPathPolicy.OutputTailBytes];
    private int headFilled;
    private int tailFilled;
    private int tailWriteIndex;
    private long total;

    public long TotalBytes => total;

    public bool Truncated => total > SandboxPathPolicy.MaxCapturedOutputBytes;

    public void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (headFilled < head.Length)
            {
                var take = Math.Min(head.Length - headFilled, data.Length);
                data[..take].CopyTo(head.AsSpan(headFilled));
                headFilled += take;
                total += take;
                data = data[take..];
                continue;
            }

            var chunk = Math.Min(data.Length, tail.Length);
            var slice = data[..chunk];
            foreach (var value in slice)
            {
                tail[tailWriteIndex] = value;
                tailWriteIndex = (tailWriteIndex + 1) % tail.Length;
                if (tailFilled < tail.Length)
                {
                    tailFilled++;
                }
            }

            total += chunk;
            data = data[chunk..];
        }
    }

    public string ToUtf8String()
    {
        if (total <= headFilled)
        {
            return Encoding.UTF8.GetString(head, 0, headFilled);
        }

        if (!Truncated)
        {
            var combined = new byte[headFilled + tailFilled];
            Buffer.BlockCopy(head, 0, combined, 0, headFilled);
            Buffer.BlockCopy(tail, 0, combined, headFilled, tailFilled);
            return Encoding.UTF8.GetString(combined);
        }

        var orderedTail = new byte[tailFilled];
        var start = tailFilled == tail.Length ? tailWriteIndex : 0;
        for (var i = 0; i < tailFilled; i++)
        {
            orderedTail[i] = tail[(start + i) % tail.Length];
        }

        return Encoding.UTF8.GetString(head, 0, headFilled) + Encoding.UTF8.GetString(orderedTail);
    }
}
