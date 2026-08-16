using System.Net;
using System.Text;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Infrastructure.Providers;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp14InfrastructureTests()
    {
        Run(nameof(OpenAiResponsesParsesTextToolRefusalIncompleteAndUsage), OpenAiResponsesParsesTextToolRefusalIncompleteAndUsage);
        Run(nameof(AnthropicParsesSystemMappingToolsAndCacheUsage), AnthropicParsesSystemMappingToolsAndCacheUsage);
        Run(nameof(CompatibleChatParsesToolsCacheAndThinkingExtension), CompatibleChatParsesToolsCacheAndThinkingExtension);
        Run(nameof(SseParserHandlesFragmentationAndUnknownEvents), SseParserHandlesFragmentationAndUnknownEvents);
        Run(nameof(HttpErrorsClassifyExactly), HttpErrorsClassifyExactly);
        Run(nameof(SensitiveHeadersAreRedactedAndDefinitionsOmitSecrets), SensitiveHeadersAreRedactedAndDefinitionsOmitSecrets);
        Run(nameof(StreamEofBeforeTerminalIsIncomplete), StreamEofBeforeTerminalIsIncomplete);
    }

    private static PromptIr SampleIr(bool tools = false)
    {
        var compiled = PromptCompiler.Compile(new PromptCompileRequest(
            PromptCompilerVersions.Current,
            "writer",
            "role",
            "behavior",
            BehavioralOverrideMode.Default,
            null,
            ContentBehaviorMode.Sfw,
            ["project"],
            [],
            "workflow",
            "task",
            [],
            [],
            "hello",
            tools ? [new AuthorizedToolSchema("lookup", "lookup", "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}", ["q"])] : [],
            new PromptOutputContract(OutputContractKind.PlainText, null, []),
            null,
            64));
        return compiled.Ir!;
    }

    private static void OpenAiResponsesParsesTextToolRefusalIncompleteAndUsage()
    {
        var text = OpenAiResponsesAdapter.Parse(Json(200, """
            {"id":"resp_1","model":"gpt-test","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]}],"usage":{"input_tokens":11,"output_tokens":2,"input_tokens_details":{"cached_tokens":4}}}
            """));
        AssertEqual(InvocationLifecycle.Completed.ToString(), text.Lifecycle.ToString(), "OpenAI text not completed.");
        AssertEqual(11L, text.Usage.InputTokens.Value.GetValueOrDefault(), "OpenAI input tokens lost.");
        AssertEqual(4L, text.Usage.CachedInputReadTokens.Value.GetValueOrDefault(), "Cached usage lost.");

        var tools = OpenAiResponsesAdapter.Parse(Json(200, """
            {"id":"resp_2","status":"completed","output":[{"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"q\":\"a\"}"}]}
            """));
        AssertEqual("lookup", tools.CompletedToolCalls.Single().ToolName, "Tool call dropped.");

        var refusal = OpenAiResponsesAdapter.Parse(Json(200, """
            {"id":"resp_3","status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}
            """));
        AssertEqual(InvocationFailureClass.ProviderRefusal.ToString(), refusal.FailureClass.ToString(), "Refusal not classified.");

        var incomplete = OpenAiResponsesAdapter.Parse(Json(200, """
            {"id":"resp_4","status":"incomplete","output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}]}
            """));
        AssertEqual(InvocationLifecycle.Incomplete.ToString(), incomplete.Lifecycle.ToString(), "Incomplete treated as completed.");

        var malformed = OpenAiResponsesAdapter.Parse(Json(200, "{not-json"));
        AssertEqual(InvocationFailureClass.MalformedProtocol.ToString(), malformed.FailureClass.ToString(), "Malformed accepted.");

        var missing = OpenAiResponsesAdapter.Parse(Json(200, "{\"id\":\"x\"}"));
        AssertEqual(InvocationFailureClass.MalformedProtocol.ToString(), missing.FailureClass.ToString(), "Missing output accepted.");
    }

    private static void AnthropicParsesSystemMappingToolsAndCacheUsage()
    {
        var parsed = AnthropicMessagesAdapter.Parse(Json(200, """
            {"id":"msg_1","model":"claude-test","content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"toolu_1","name":"lookup","input":{"q":"a"}},{"type":"thinking","thinking":"hidden"}],"usage":{"input_tokens":9,"output_tokens":3,"cache_read_input_tokens":5,"cache_creation_input_tokens":2}}
            """));
        AssertEqual(InvocationLifecycle.Completed.ToString(), parsed.Lifecycle.ToString(), "Anthropic not completed.");
        AssertEqual("lookup", parsed.CompletedToolCalls.Single().ToolName, "tool_use dropped.");
        AssertEqual(5L, parsed.Usage.CachedInputReadTokens.Value.GetValueOrDefault(), "Anthropic cache read lost.");
        AssertEqual(2L, parsed.Usage.CacheWriteTokens.Value.GetValueOrDefault(), "Anthropic cache write lost.");
        AssertTrue(parsed.Events.All(item => item.Kind != ModelRuntimeEventKind.ReasoningSummary || item.Text != "hidden"),
            "Raw thinking stored as product event.");
        var malformed = AnthropicMessagesAdapter.Parse(Json(200, "{\"id\":\"msg\"}"));
        AssertEqual(InvocationFailureClass.MalformedProtocol.ToString(), malformed.FailureClass.ToString(), "Missing content accepted.");
    }

    private static void CompatibleChatParsesToolsCacheAndThinkingExtension()
    {
        var parsed = OpenAiCompatibleChatAdapter.Parse(Json(200, """
            {"id":"chatcmpl_1","model":"deepseek-v4-pro","choices":[{"message":{"role":"assistant","content":"ok","reasoning_content":"hidden-cot","tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"prompt_cache_hit_tokens":7,"prompt_cache_miss_tokens":3,"completion_tokens_details":{"reasoning_tokens":4}},"mystery_extension":true}
            """));
        AssertEqual("lookup", parsed.CompletedToolCalls.Single().ToolName, "Compatible tool call dropped.");
        AssertEqual(7L, parsed.Usage.CachedInputReadTokens.Value.GetValueOrDefault(), "DeepSeek cache hit lost.");
        AssertEqual(4L, parsed.Usage.ReasoningTokens.Value.GetValueOrDefault(), "Reasoning tokens lost.");
        AssertTrue(parsed.Events.All(item => item.Text != "hidden-cot"), "reasoning_content became Result text.");
        var unknownRequired = OpenAiCompatibleChatAdapter.Parse(Json(200, "{\"id\":\"x\",\"object\":\"chat.completion\"}"));
        AssertEqual(InvocationFailureClass.MalformedProtocol.ToString(), unknownRequired.FailureClass.ToString(), "Missing choices accepted.");
    }

    private static void SseParserHandlesFragmentationAndUnknownEvents()
    {
        var parser = new SseEventParser();
        var payload = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"A\"}\n\nevent: weird.new\ndata: {\"type\":\"weird.new\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\"}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var frames = new List<SseFrame>();
        foreach (var b in bytes)
        {
            frames.AddRange(parser.Push([b]));
        }

        frames.AddRange(parser.Finish());
        AssertEqual(3, frames.Count, "SSE frame count drifted.");
        AssertEqual("response.output_text.delta", frames[0].EventType, "First event lost.");
        AssertEqual("weird.new", frames[1].EventType, "Unknown event dropped instead of forwarded.");
        AssertEqual("response.completed", frames[2].EventType, "Terminal event lost.");

        var crlf = new SseEventParser();
        var crlfFrames = crlf.Push("data: hi\r\n\r\n"u8);
        AssertEqual("hi", crlfFrames[0].Data, "CRLF not accepted.");

        var splitJson = new SseEventParser();
        var a = splitJson.Push("data: {\"a\":"u8);
        var secondChunk = splitJson.Push("1}\n\n"u8);
        AssertEqual(0, a.Count, "Partial JSON flushed early.");
        AssertEqual("{\"a\":1}", secondChunk[0].Data, "Split JSON not reassembled.");

        var unicode = new SseEventParser();
        var unicodeFrames = new List<SseFrame>();
        foreach (var unit in Encoding.UTF8.GetBytes("data: café\n\n"))
        {
            unicodeFrames.AddRange(unicode.Push([unit]));
        }

        unicodeFrames.AddRange(unicode.Finish());
        AssertEqual("café", unicodeFrames[0].Data, "Split UTF-8 not reassembled.");
    }

    private static void HttpErrorsClassifyExactly()
    {
        AssertEqual(InvocationLifecycle.Rejected.ToString(), InvocationStateMachine.AfterHttpStatus(400).ToString(), "HTTP 400 not Rejected.");
        AssertEqual(InvocationFailureClass.HttpUnauthorized.ToString(), InvocationStateMachine.ClassifyHttp(401).ToString(), "HTTP 401 drifted.");
        AssertEqual(InvocationFailureClass.HttpRateLimited.ToString(), InvocationStateMachine.ClassifyHttp(429).ToString(), "HTTP 429 drifted.");
        AssertEqual(InvocationFailureClass.HttpServerError.ToString(), InvocationStateMachine.ClassifyHttp(500).ToString(), "HTTP 500 drifted.");
        AssertEqual(InvocationLifecycle.OutcomeUnknown.ToString(), InvocationStateMachine.TimeoutAfterPossibleSend().ToString(), "Timeout not unknown.");

        var handler = new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate\"}")
        });
        var transport = new ProviderHttpTransport(handler);
        var result = transport.SendAsync(
            new Uri("https://api.example.com/v1/responses"),
            HttpMethod.Post,
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + Wp14Canary() },
            "{}",
            TimeSpan.FromSeconds(5),
            CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(429, result.StatusCode, "429 not captured.");
        AssertEqual(InvocationFailureClass.HttpRateLimited.ToString(), result.FailureClass.ToString(), "429 classification drifted.");
        AssertTrue(!result.RedactedDiagnostics.Contains(Wp14Canary(), StringComparison.Ordinal), "Bearer leaked.");
        AssertTrue(result.RedactedDiagnostics.Contains("<redacted>", StringComparison.Ordinal), "Redaction marker missing.");

        var connectHandler = new ScriptedHttpMessageHandler(_ => throw new HttpRequestException("connect failed"));
        var connect = new ProviderHttpTransport(connectHandler).SendAsync(
            new Uri("https://api.example.com/v1/responses"),
            HttpMethod.Post,
            new Dictionary<string, string>(),
            "{}",
            TimeSpan.FromSeconds(5),
            CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(InvocationLifecycle.OutcomeUnknown.ToString(), connect.Lifecycle.ToString(), "Connect failure claimed not-executed.");
        AssertEqual(InvocationFailureClass.TransportOutcomeUnknown.ToString(), connect.FailureClass.ToString(), "Connect failure classified as timeout.");
        AssertTrue(connect.DuplicateExecutionRisk, "Unknown send must record duplicate-execution risk.");
    }

    private static void SensitiveHeadersAreRedactedAndDefinitionsOmitSecrets()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/responses");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Wp14Canary());
        request.Headers.TryAddWithoutValidation("x-api-key", Wp14Canary());
        var described = HeaderRedaction.Describe(request);
        AssertTrue(!described.Contains(Wp14Canary(), StringComparison.Ordinal), "Header describe leaked canary.");
        var path = Path.Combine(Path.GetTempPath(), "llmw-wp14", Guid.NewGuid().ToString("N"), "providers.json");
        var store = new FileProviderDefinitionStore(path);
        store.Upsert(ProviderDefinitionFactory.Create("p1", ProtocolKind.OpenAiResponses, "https://api.example.com/v1", "cred-ref", "m1", allowInsecureLocalHttp: false));
        var json = File.ReadAllText(path);
        AssertTrue(!json.Contains(Wp14Canary(), StringComparison.Ordinal), "File store leaked canary.");
        AssertTrue(json.Contains("cred-ref", StringComparison.Ordinal), "CredentialRef missing.");
        AssertTrue(!json.Contains("Bearer", StringComparison.Ordinal), "Secret header persisted.");
    }

    private static void StreamEofBeforeTerminalIsIncomplete()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent("event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n");
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        var adapter = new OpenAiResponsesAdapter(new ProviderHttpTransport(handler));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.OpenAiResponses, "https://api.example.com", "cred", "m", allowInsecureLocalHttp: false);
        using var secret = new ResolvedProviderSecret("token");
        var events = new List<ModelRuntimeEvent>();
        var enumerator = adapter.StreamAsync(
            definition,
            ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!,
            secret,
            new ProviderInvokeRequest(SampleIr(), new ModelId("m"), true, new Dictionary<string, string>()),
            CancellationToken.None).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                events.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        AssertTrue(events.Any(item => item.Kind == ModelRuntimeEventKind.Incomplete && item.ErrorCode == "eof-before-terminal"),
            "EOF before terminal not Incomplete.");
    }

    private static ProviderHttpResult Json(int status, string body) =>
        new((InvocationLifecycle)0, InvocationFailureClass.None, status, "req", Encoding.UTF8.GetBytes(body), "application/json", "redacted", false);

    private static string Wp14Canary() => "sk-wp14-canary-9f3a2c";
}
