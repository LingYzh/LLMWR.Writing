using System.Text;
using System.Text.Json;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed record SseFrame(string EventType, string Data, bool Heartbeat);

public sealed class SseEventParser
{
    private readonly List<byte> buffer = [];
    private string currentEvent = "message";
    private readonly StringBuilder data = new();

    public List<SseFrame> Push(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk)
        {
            buffer.Add(b);
        }

        return Drain(endOfStream: false);
    }

    public List<SseFrame> Finish() => Drain(endOfStream: true);

    private List<SseFrame> Drain(bool endOfStream)
    {
        var frames = new List<SseFrame>();
        while (TryReadLine(endOfStream, out var line))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 || !string.Equals(currentEvent, "message", StringComparison.Ordinal))
                {
                    frames.Add(new SseFrame(currentEvent, data.ToString(), data.Length == 0));
                }

                currentEvent = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }
            }

            if (field == "event")
            {
                currentEvent = value;
            }
            else if (field == "data")
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
        }

        return frames;
    }

    private bool TryReadLine(bool endOfStream, out string line)
    {
        line = "";
        for (var i = 0; i < buffer.Count; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                var length = i;
                if (length > 0 && buffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                line = Encoding.UTF8.GetString(buffer.GetRange(0, length).ToArray());
                var remove = i + 1;
                buffer.RemoveRange(0, remove);
                return true;
            }
        }

        if (endOfStream && buffer.Count > 0)
        {
            line = Encoding.UTF8.GetString(buffer.ToArray());
            buffer.Clear();
            return true;
        }

        return false;
    }
}

public static class HeaderRedaction
{
    public static bool IsSensitive(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api-key", StringComparison.OrdinalIgnoreCase);

    public static string Describe(HttpRequestMessage request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(request.RequestUri);
        foreach (var header in request.Headers)
        {
            builder.Append(" [").Append(header.Key).Append('=');
            builder.Append(IsSensitive(header.Key) ? "<redacted>" : string.Join(',', header.Value));
            builder.Append(']');
        }

        return builder.ToString();
    }
}

internal static class SseJson
{
    public static bool TryParse(string data, out JsonDocument document, out ModelRuntimeEvent error)
    {
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
            error = null!;
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            error = new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, "malformed-sse", true);
            return false;
        }
    }
}
