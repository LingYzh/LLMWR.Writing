using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed class ProviderHttpTransport
{
    private readonly HttpMessageHandler handler;

    public ProviderHttpTransport(HttpMessageHandler handler)
    {
        this.handler = handler;
        if (handler is HttpClientHandler clientHandler)
        {
            clientHandler.AllowAutoRedirect = false;
        }

        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
        }
    }

    public async Task<ProviderHttpResult> SendAsync(
        Uri uri,
        HttpMethod method,
        IReadOnlyDictionary<string, string> headers,
        string? jsonBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout,
            DefaultRequestHeaders = { ExpectContinue = false }
        };
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            if (header.Value.Contains('\r', StringComparison.Ordinal) || header.Value.Contains('\n', StringComparison.Ordinal) ||
                header.Key.Contains('\r', StringComparison.Ordinal) || header.Key.Contains('\n', StringComparison.Ordinal))
            {
                return ProviderHttpResult.BeforeSend("HEADER_CRLF_FORBIDDEN");
            }

            if (IsReserved(header.Key) && !HeaderRedaction.IsSensitive(header.Key))
            {
                return ProviderHttpResult.BeforeSend("RESERVED_HEADER");
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProviderHttpResult.AfterPossibleSend(InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote, true);
        }
        catch (TaskCanceledException)
        {
            return ProviderHttpResult.AfterPossibleSend(InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TimeoutOutcomeUnknown, true);
        }
        catch (HttpRequestException)
        {
            return ProviderHttpResult.AfterPossibleSend(InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TransportOutcomeUnknown, true);
        }

        var requestId = response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var media = response.Content.Headers.ContentType?.MediaType;
        return new ProviderHttpResult(
            InvocationStateMachine.AfterHttpStatus(status),
            InvocationStateMachine.ClassifyHttp(status),
            status,
            requestId,
            body,
            media,
            HeaderRedaction.Describe(request),
            false);
    }

    public async IAsyncEnumerable<SseReadResult> ReadSseAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string jsonBody,
        TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        HttpResponseMessage? response = null;
        ProviderHttpResult? sendFailure = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sendFailure = ProviderHttpResult.AfterPossibleSend(
                InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote, true);
        }
        catch (TaskCanceledException)
        {
            sendFailure = ProviderHttpResult.AfterPossibleSend(
                InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TimeoutOutcomeUnknown, true);
        }
        catch (HttpRequestException)
        {
            sendFailure = ProviderHttpResult.AfterPossibleSend(
                InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TransportOutcomeUnknown, true);
        }

        if (sendFailure is not null || response is null)
        {
            yield return new SseReadResult(
                null,
                sendFailure ?? ProviderHttpResult.AfterPossibleSend(
                    InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TransportOutcomeUnknown, true));
            yield break;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                var errorBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var media = response.Content.Headers.ContentType?.MediaType;
                var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                    ? values.FirstOrDefault()
                    : null;
                yield return new SseReadResult(
                    null,
                    new ProviderHttpResult(
                        InvocationStateMachine.AfterHttpStatus(status),
                        InvocationStateMachine.ClassifyHttp(status),
                        status,
                        requestId,
                        errorBody,
                        media,
                        HeaderRedaction.Describe(request),
                        false));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var parser = new SseEventParser();
            var buffer = new byte[1];
            while (true)
            {
                int read = 0;
                ProviderHttpResult? readFailure = null;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    readFailure = ProviderHttpResult.AfterPossibleSend(
                        InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote, true);
                }
                catch (TaskCanceledException)
                {
                    readFailure = ProviderHttpResult.AfterPossibleSend(
                        InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TimeoutOutcomeUnknown, true);
                }
                catch (HttpRequestException)
                {
                    readFailure = ProviderHttpResult.AfterPossibleSend(
                        InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TransportOutcomeUnknown, true);
                }

                if (readFailure is not null)
                {
                    yield return new SseReadResult(null, readFailure);
                    yield break;
                }

                var frames = read == 0 ? parser.Finish() : parser.Push(buffer.AsSpan(0, read));
                if (parser.FailureCode is not null)
                {
                    yield return new SseReadResult(
                        null,
                        ProviderHttpResult.BeforeSend(parser.FailureCode) with
                        {
                            Lifecycle = InvocationLifecycle.Rejected,
                            FailureClass = InvocationFailureClass.MalformedProtocol
                        });
                    yield break;
                }

                foreach (var frame in frames)
                {
                    yield return new SseReadResult(frame, null);
                }

                if (read == 0)
                {
                    yield break;
                }
            }
        }
    }

    private static bool IsReserved(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
}

public sealed record ProviderHttpResult(
    InvocationLifecycle Lifecycle,
    InvocationFailureClass FailureClass,
    int StatusCode,
    string? ProviderRequestId,
    byte[] Body,
    string? MediaType,
    string RedactedDiagnostics,
    bool DuplicateExecutionRisk)
{
    public static ProviderHttpResult BeforeSend(string code) =>
        new(InvocationLifecycle.FailedBeforeSend, InvocationFailureClass.FailedBeforeSend, 0, null, Encoding.UTF8.GetBytes(code), null, code, false);

    public static ProviderHttpResult AfterPossibleSend(InvocationLifecycle lifecycle, InvocationFailureClass failure, bool duplicateRisk) =>
        new(lifecycle, failure, 0, null, [], null, failure.ToString(), duplicateRisk);

    public string BodyText => Encoding.UTF8.GetString(Body);
}

public static class PromptWireMapping
{
    public static IReadOnlyList<PromptBlock> InstructionBlocks(PromptIr ir) =>
        ir.OrderedBlocks.Where(block =>
            block.Layer is PromptLayer.RuntimePolicy or PromptLayer.BaseRole or PromptLayer.Behavioral or PromptLayer.ContentOverlay &&
            block.SemanticRole == PromptSemanticRole.Instruction).ToArray();

    public static IReadOnlyList<PromptBlock> ContextBlocks(PromptIr ir) =>
        ir.OrderedBlocks.Where(block =>
            block.Layer is PromptLayer.ProjectContext or PromptLayer.Skills or PromptLayer.Workflow or PromptLayer.Task or PromptLayer.User &&
            block.SourceKind != PromptSourceKind.ToolContinuationProvenance)
            .ToArray();

    public static string JoinInstructions(PromptIr ir) =>
        string.Join("\n\n", InstructionBlocks(ir).Select(block => block.Content));

    public static string JoinContext(PromptIr ir)
    {
        var builder = new StringBuilder();
        foreach (var block in ContextBlocks(ir))
        {
            builder.Append('[').Append(block.Layer).Append('/').Append(block.SemanticRole).Append("]\n");
            builder.Append(block.Content).Append("\n\n");
        }

        return builder.ToString();
    }
}

public static class UsageNormalizer
{
    public static NormalizedUsage FromOpenAi(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return NormalizedUsage.Unknown;
        }

        var input = Token(usage, "input_tokens") ?? Token(usage, "prompt_tokens");
        var output = Token(usage, "output_tokens") ?? Token(usage, "completion_tokens");
        long? cached = null;
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.TryGetProperty("cached_tokens", out var cachedEl) && cachedEl.TryGetInt64(out var cachedValue))
        {
            cached = cachedValue;
        }

        cached ??= Token(usage, "prompt_cache_hit_tokens");
        var cacheMiss = Token(usage, "prompt_cache_miss_tokens");
        long? reasoning = null;
        if (usage.TryGetProperty("output_tokens_details", out var outDetails) && outDetails.TryGetProperty("reasoning_tokens", out var r) && r.TryGetInt64(out var rv))
        {
            reasoning = rv;
        }

        reasoning ??= Token(usage, "reasoning_tokens");
        if (usage.TryGetProperty("completion_tokens_details", out var ctd) && ctd.TryGetProperty("reasoning_tokens", out var cr) && cr.TryGetInt64(out var crv))
        {
            reasoning = crv;
        }

        var extras = new Dictionary<string, long>(StringComparer.Ordinal);
        if (cacheMiss is long miss)
        {
            extras["prompt_cache_miss_tokens"] = miss;
        }

        return new NormalizedUsage(
            UsageStatus.Reported,
            input is long i ? OptionalTokenCount.Reported(i) : OptionalTokenCount.Unknown,
            cached is long c ? OptionalTokenCount.Reported(c) : OptionalTokenCount.Unknown,
            OptionalTokenCount.Unknown,
            output is long o ? OptionalTokenCount.Reported(o) : OptionalTokenCount.Unknown,
            reasoning is long rs ? OptionalTokenCount.Reported(rs) : OptionalTokenCount.Unknown,
            extras,
            usage.GetRawText());
    }

    public static NormalizedUsage FromAnthropic(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return NormalizedUsage.Unknown;
        }

        return FromAnthropicUsage(usage);
    }

    public static NormalizedUsage FromAnthropicUsage(JsonElement usage)
    {
        var extras = new Dictionary<string, long>(StringComparer.Ordinal);
        var cacheRead = Token(usage, "cache_read_input_tokens");
        var cacheCreate = Token(usage, "cache_creation_input_tokens");
        if (cacheRead is long read)
        {
            extras["cache_read_input_tokens"] = read;
        }

        if (cacheCreate is long create)
        {
            extras["cache_creation_input_tokens"] = create;
        }

        var input = Token(usage, "input_tokens");
        var output = Token(usage, "output_tokens");
        return new NormalizedUsage(
            UsageStatus.Reported,
            input is long i ? OptionalTokenCount.Reported(i) : OptionalTokenCount.Unknown,
            cacheRead is long cr ? OptionalTokenCount.Reported(cr) : OptionalTokenCount.Unknown,
            cacheCreate is long cc ? OptionalTokenCount.Reported(cc) : OptionalTokenCount.Unknown,
            output is long o ? OptionalTokenCount.Reported(o) : OptionalTokenCount.Unknown,
            OptionalTokenCount.Unknown,
            extras,
            usage.GetRawText());
    }

    private static long? Token(JsonElement usage, string name)
    {
        if (usage.TryGetProperty(name, out var property) && property.TryGetInt64(out var value))
        {
            return value;
        }

        return null;
    }
}
