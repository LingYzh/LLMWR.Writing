using System.Net;
using System.Text;
using System.Text.Json;
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
        Run(nameof(OpenAiUsesStableClientRequestIdAndStreamHttpTaxonomy), OpenAiUsesStableClientRequestIdAndStreamHttpTaxonomy);
        Run(nameof(SseParserEnforcesBounds), SseParserEnforcesBounds);
        Run(nameof(AnthropicStopReasonsAndThinkingModes), AnthropicStopReasonsAndThinkingModes);
        Run(nameof(DeepSeekThinkingToolsAreUnsupportedWhileThinkingAloneParses), DeepSeekThinkingToolsAreUnsupportedWhileThinkingAloneParses);
        Run(nameof(ProviderDefinitionRoundTripAndRevisionAdvance), ProviderDefinitionRoundTripAndRevisionAdvance);
        Run(nameof(WireDigestChangesWithSemanticRequestDimensions), WireDigestChangesWithSemanticRequestDimensions);
        Run(nameof(AnthropicAdaptiveThinkingExactJsonFixture), AnthropicAdaptiveThinkingExactJsonFixture);
        Run(nameof(AnthropicManualThinkingBudgetBounds), AnthropicManualThinkingBudgetBounds);
        Run(nameof(NativeToolContinuationIsProviderOwned), NativeToolContinuationIsProviderOwned);
        Run(nameof(AnthropicStreamMergesMessageStartUsage), AnthropicStreamMergesMessageStartUsage);
        Run(nameof(CompatibleChatThinkingWithToolsHonorsCapability), CompatibleChatThinkingWithToolsHonorsCapability);
    }

    private static PromptIr SampleIr(bool tools = false, int reserved = 64)
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
            reserved));
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

    private static void OpenAiUsesStableClientRequestIdAndStreamHttpTaxonomy()
    {
        string? captured = null;
        var handler = new ScriptedHttpMessageHandler(request =>
        {
            captured = request.Headers.TryGetValues("X-Client-Request-Id", out var values) ? values.FirstOrDefault() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"r\",\"output\":[],\"status\":\"completed\"}", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiResponsesAdapter(new ProviderHttpTransport(handler));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.OpenAiResponses, "https://api.example.com", "cred", "m", allowInsecureLocalHttp: false);
        using var secret = new ResolvedProviderSecret("token");
        var ir = SampleIr();
        _ = adapter.InvokeAsync(definition, ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!, secret,
            new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string>(), "inv-stable"),
            CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual("inv-stable", captured ?? "", "X-Client-Request-Id was not the frozen InvocationId.");

        foreach (var (status, expected) in new[]
                 {
                     (HttpStatusCode.Unauthorized, InvocationFailureClass.HttpUnauthorized),
                     (HttpStatusCode.TooManyRequests, InvocationFailureClass.HttpRateLimited),
                     (HttpStatusCode.InternalServerError, InvocationFailureClass.HttpServerError)
                 })
        {
            var streamHandler = new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json")
            });
            var streamAdapter = new OpenAiResponsesAdapter(new ProviderHttpTransport(streamHandler));
            var events = Drain(streamAdapter.StreamAsync(
                definition,
                ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!,
                secret,
                new ProviderInvokeRequest(ir, new ModelId("m"), true, new Dictionary<string, string>(), "inv-stream"),
                CancellationToken.None));
            AssertTrue(events.Any(item => item.Terminal && item.ErrorCode == expected.ToString()),
                status + " stream was not classified as " + expected);
        }
    }

    private static void SseParserEnforcesBounds()
    {
        var parser = new SseEventParser();
        var linePayload = new string('x', 32 * 1024);
        var builder = new StringBuilder();
        while (builder.Length < SseEventParser.MaxEventDataBytes + 4096)
        {
            builder.Append("data: ").Append(linePayload).Append('\n');
        }

        builder.Append('\n');
        _ = parser.Push(Encoding.UTF8.GetBytes(builder.ToString()));
        AssertEqual("PROTOCOL_SSE_EVENT_TOO_LARGE", parser.FailureCode ?? "", "Unbounded SSE event data accepted.");
        var line = new SseEventParser();
        _ = line.Push(Encoding.UTF8.GetBytes(new string('a', SseEventParser.MaxLineBytes + 4)));
        AssertEqual("PROTOCOL_SSE_LINE_TOO_LARGE", line.FailureCode ?? "", "Unbounded SSE line accepted.");
    }

    private static void AnthropicStopReasonsAndThinkingModes()
    {
        AssertEqual(InvocationLifecycle.Completed.ToString(), AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn"}""")).Lifecycle.ToString(), "end_turn not completed.");
        AssertEqual(InvocationLifecycle.Incomplete.ToString(), AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"text","text":"hi"}],"stop_reason":"max_tokens"}""")).Lifecycle.ToString(), "max_tokens not incomplete.");
        AssertEqual(InvocationLifecycle.Incomplete.ToString(), AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"text","text":"hi"}],"stop_reason":"model_context_window_exceeded"}""")).Lifecycle.ToString(), "context window not incomplete.");
        AssertEqual(InvocationFailureClass.ProviderRefusal.ToString(), AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"text","text":"no"}],"stop_reason":"refusal"}""")).FailureClass.ToString(), "refusal drifted.");
        AssertEqual("tool_use", AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"tool_use","id":"t1","name":"lookup","input":{}}],"stop_reason":"tool_use"}""")).ErrorCode ?? "", "tool_use not a model subturn.");
        AssertEqual("pause_turn", AnthropicMessagesAdapter.Parse(Json(200, """{"id":"m","content":[{"type":"text","text":"wait"}],"stop_reason":"pause_turn"}""")).ErrorCode ?? "", "pause_turn drifted.");

        var adapter = new AnthropicMessagesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.AnthropicMessages, "https://api.anthropic.com", "cred", "m", allowInsecureLocalHttp: false);
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!;
        var ir = SampleIr(reserved: 4096);
        var profile = AnthropicThinkingProfile();
        var enabled = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled", ["thinkingBudgetTokens"] = "2048" }, "inv", ProtocolProfile: profile));
        AssertTrue(enabled.Succeeded, "Manual thinking with budget rejected.");
        AssertTrue(enabled.Request!.CanonicalSemanticBody.Contains("budget_tokens", StringComparison.Ordinal), "budget_tokens missing.");
        var adaptive = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "adaptive", ["thinkingEffort"] = "high" }, "inv", ProtocolProfile: profile));
        AssertTrue(adaptive.Succeeded, "Adaptive thinking rejected.");
        AssertTrue(adaptive.Request!.CanonicalSemanticBody.Contains("adaptive", StringComparison.Ordinal), "adaptive thinking missing.");
        var invalid = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled" }, "inv", ProtocolProfile: profile));
        AssertEqual("THINKING_BUDGET_INVALID", invalid.ErrorCode ?? "", "Invalid thinking was sent.");
        var unsupported = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled", ["thinkingBudgetTokens"] = "2048" }, "inv"));
        AssertEqual("THINKING_UNSUPPORTED", unsupported.ErrorCode ?? "", "Uncertified thinking was sent.");
    }

    private static void DeepSeekThinkingToolsAreUnsupportedWhileThinkingAloneParses()
    {
        var adapter = new OpenAiCompatibleChatAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create(
            "p", ProtocolKind.OpenAiCompatibleChatCompletions, "https://api.deepseek.com", "cred", "m",
            allowInsecureLocalHttp: false, extensions: new Dictionary<string, string> { ["thinking"] = "enabled" });
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!;
        var withTools = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(SampleIr(tools: true), new ModelId("m"), false, definition.AdapterExtensions, "inv"));
        AssertEqual("THINKING_WITH_TOOLS_UNSUPPORTED", withTools.ErrorCode ?? "", "Thinking+tools was advertised.");
        var noTools = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(SampleIr(), new ModelId("m"), false, definition.AdapterExtensions, "inv"));
        AssertTrue(noTools.Succeeded, "Thinking without tools was blocked.");
        var parsed = OpenAiCompatibleChatAdapter.Parse(Json(200, """
            {"id":"chatcmpl_think","choices":[{"message":{"role":"assistant","content":"ok","reasoning_content":"hidden-cot"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"completion_tokens_details":{"reasoning_tokens":9}}}
            """));
        AssertEqual(9L, parsed.Usage.ReasoningTokens.Value.GetValueOrDefault(), "Reasoning tokens dropped.");
        AssertTrue(parsed.Events.All(item => item.Text != "hidden-cot"), "reasoning_content became a product event.");
    }

    private static void ProviderDefinitionRoundTripAndRevisionAdvance()
    {
        var path = Path.Combine(Path.GetTempPath(), "llmw-wp14", Guid.NewGuid().ToString("N"), "providers.json");
        var store = new FileProviderDefinitionStore(path);
        var original = new ProviderDefinitionV1(
            new ProviderDefinitionId("p1"),
            new ProviderRevision(1),
            "One",
            true,
            ProtocolKind.OpenAiResponses,
            "https://api.example.com/v1",
            new CredentialRef("cred-ref"),
            new ModelId("m1"),
            new Dictionary<string, string> { ["alias"] = "m1-alias" },
            30_000,
            ProviderDataBehavior.StatelessClientManaged,
            2,
            "price-1",
            new Dictionary<string, string> { ["thinking"] = "enabled" },
            false);
        store.Upsert(original);
        var reloaded = store.FindById(new ProviderDefinitionId("p1"))!;
        AssertEqual(original.PriceSnapshotId ?? "", reloaded.PriceSnapshotId ?? "", "PriceSnapshotId dropped.");
        AssertEqual("m1-alias", reloaded.ModelAliases["alias"], "modelAliases dropped.");
        AssertEqual(1, reloaded.Revision.Value, "First insert revision drifted.");
        store.Upsert(reloaded with { DisplayName = "Still One" });
        AssertEqual(1, store.FindById(new ProviderDefinitionId("p1"))!.Revision.Value, "Idempotent update advanced revision.");
        store.Upsert(reloaded with { Endpoint = "https://api.example.com/v2", Revision = new ProviderRevision(1) });
        AssertEqual(2, store.FindById(new ProviderDefinitionId("p1"))!.Revision.Value, "Semantic endpoint change did not advance revision.");
    }

    private static void WireDigestChangesWithSemanticRequestDimensions()
    {
        var adapter = new OpenAiResponsesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.OpenAiResponses, "https://api.example.com", "cred", "m", allowInsecureLocalHttp: false);
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!;
        var ir = SampleIr();
        string Digest(ProviderInvokeRequest request)
        {
            var prepared = adapter.Prepare(definition, endpoint, request).Request!;
            return PromptDigests.WireRequestDigestFromPrepared(
                adapter.AdapterId, adapter.AdapterVersion, prepared.Method, prepared.Path, prepared.Stream,
                prepared.CanonicalSemanticBody, prepared.NonSecretHeaders);
        }

        var baseline = new ProviderInvokeRequest(ir, new ModelId("m"), false, new Dictionary<string, string>(), "inv-a");
        var baseDigest = Digest(baseline);
        AssertTrue(baseDigest != Digest(baseline with { Stream = true }), "stream did not change digest.");
        AssertTrue(baseDigest != Digest(baseline with { ModelId = new ModelId("m2") }), "model did not change digest.");
        AssertTrue(baseDigest != Digest(new ProviderInvokeRequest(SampleIr() with { ReservedOutputTokens = 128 }, new ModelId("m"), false, new Dictionary<string, string>(), "inv-a")),
            "max_tokens did not change digest.");
        AssertTrue(baseDigest != Digest(baseline with { AdapterExtensions = new Dictionary<string, string> { ["reasoningEffort"] = "high" } }),
            "reasoning effort did not change digest.");
        AssertTrue(baseDigest != Digest(new ProviderInvokeRequest(SampleIr(tools: true), new ModelId("m"), false, new Dictionary<string, string>(), "inv-a")),
            "tool schema did not change digest.");
        var structured = PromptCompiler.Compile(new PromptCompileRequest(
            PromptCompilerVersions.Current, "writer", "role", "behavior", BehavioralOverrideMode.Default, null,
            ContentBehaviorMode.Sfw, ["project"], [], "workflow", "task", [], [], "hello", [],
            new PromptOutputContract(OutputContractKind.StructuredJson, "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}},\"required\":[\"x\"],\"additionalProperties\":false}", ["x"]),
            null, 64)).Ir!;
        AssertTrue(baseDigest != Digest(new ProviderInvokeRequest(structured, new ModelId("m"), false, new Dictionary<string, string> { ["strictStructuredOutput"] = "true" }, "inv-a")),
            "output schema did not change digest.");
        var secretA = Digest(baseline with { ClientRequestId = "inv-a" });
        var secretB = Digest(baseline with { ClientRequestId = "inv-b" });
        AssertEqual(secretA, secretB, "Unstable/client request id entered WireRequestDigest.");
    }

    private static void AnthropicAdaptiveThinkingExactJsonFixture()
    {
        var adapter = new AnthropicMessagesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.AnthropicMessages, "https://api.anthropic.com", "cred", "m", allowInsecureLocalHttp: false);
        var prepared = adapter.Prepare(
            definition,
            ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!,
            new ProviderInvokeRequest(
                SampleIr(reserved: 4096),
                new ModelId("m"),
                false,
                new Dictionary<string, string> { ["thinking"] = "adaptive", ["thinkingEffort"] = "high" },
                "inv",
                ProtocolProfile: AnthropicThinkingProfile()));
        AssertTrue(prepared.Succeeded, "Adaptive thinking Prepare failed.");
        using var document = JsonDocument.Parse(prepared.Request!.CanonicalSemanticBody);
        var thinking = document.RootElement.GetProperty("thinking");
        AssertEqual("adaptive", thinking.GetProperty("type").GetString() ?? "", "thinking.type drifted.");
        AssertTrue(!thinking.TryGetProperty("output_config", out _), "output_config nested inside thinking.");
        AssertEqual("high", document.RootElement.GetProperty("output_config").GetProperty("effort").GetString() ?? "", "top-level output_config.effort drifted.");
    }

    private static void AnthropicManualThinkingBudgetBounds()
    {
        var adapter = new AnthropicMessagesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.AnthropicMessages, "https://api.anthropic.com", "cred", "m", allowInsecureLocalHttp: false);
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!;
        var ir = SampleIr(reserved: 4096);
        var profile = AnthropicThinkingProfile();
        foreach (var budget in new[] { "1", "1023" })
        {
            var rejected = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(
                ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled", ["thinkingBudgetTokens"] = budget }, "inv", ProtocolProfile: profile));
            AssertEqual("THINKING_BUDGET_INVALID", rejected.ErrorCode ?? "", "budget " + budget + " was sent.");
        }

        var accepted = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(
            ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled", ["thinkingBudgetTokens"] = "1024" }, "inv", ProtocolProfile: profile));
        AssertTrue(accepted.Succeeded, "Valid >=1024 budget < max_tokens rejected.");
        var tooLarge = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(
            ir, new ModelId("m"), false, new Dictionary<string, string> { ["thinking"] = "enabled", ["thinkingBudgetTokens"] = "4096" }, "inv", ProtocolProfile: profile));
        AssertEqual("THINKING_BUDGET_INVALID", tooLarge.ErrorCode ?? "", "budget == max_tokens was sent.");
    }

    private static void NativeToolContinuationIsProviderOwned()
    {
        var turns = new[] { new ProviderNativeToolTurn("call_1", "lookup", "{\"q\":\"a\"}", "{\"ok\":true}") };
        var openai = new OpenAiResponsesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var openaiDef = ProviderDefinitionFactory.Create("p", ProtocolKind.OpenAiResponses, "https://api.example.com", "cred", "m", allowInsecureLocalHttp: false);
        var openaiBody = openai.Prepare(openaiDef, ProviderEndpoint.TryCreate(openaiDef.Endpoint, false, out _)!,
            new ProviderInvokeRequest(SampleIr(tools: true), new ModelId("m"), false, new Dictionary<string, string>(), "inv", turns)).Request!.CanonicalSemanticBody;
        AssertTrue(openaiBody.Contains("\"type\":\"function_call\"", StringComparison.Ordinal), "OpenAI function_call missing.");
        AssertTrue(openaiBody.Contains("\"type\":\"function_call_output\"", StringComparison.Ordinal), "OpenAI function_call_output missing.");
        AssertTrue(openaiBody.Contains("\"call_id\":\"call_1\"", StringComparison.Ordinal), "OpenAI call_id missing.");
        AssertTrue(!openaiBody.Contains("previous_response_id", StringComparison.Ordinal), "OpenAI required previous_response_id.");

        var anthropic = new AnthropicMessagesAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var anthropicDef = ProviderDefinitionFactory.Create("p", ProtocolKind.AnthropicMessages, "https://api.anthropic.com", "cred", "m", allowInsecureLocalHttp: false);
        var anthropicBody = anthropic.Prepare(anthropicDef, ProviderEndpoint.TryCreate(anthropicDef.Endpoint, false, out _)!,
            new ProviderInvokeRequest(SampleIr(tools: true), new ModelId("m"), false, new Dictionary<string, string>(), "inv", turns)).Request!.CanonicalSemanticBody;
        AssertTrue(anthropicBody.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal), "Anthropic tool_use missing.");
        AssertTrue(anthropicBody.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal), "Anthropic tool_result missing.");
        AssertTrue(anthropicBody.Contains("\"tool_use_id\":\"call_1\"", StringComparison.Ordinal), "Anthropic tool_use_id missing.");

        var chat = new OpenAiCompatibleChatAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var chatDef = ProviderDefinitionFactory.Create("p", ProtocolKind.OpenAiCompatibleChatCompletions, "https://api.example.com", "cred", "m", allowInsecureLocalHttp: false);
        var chatBody = chat.Prepare(chatDef, ProviderEndpoint.TryCreate(chatDef.Endpoint, false, out _)!,
            new ProviderInvokeRequest(SampleIr(tools: true), new ModelId("m"), false, new Dictionary<string, string>(), "inv", turns)).Request!.CanonicalSemanticBody;
        AssertTrue(chatBody.Contains("\"tool_calls\"", StringComparison.Ordinal), "Chat tool_calls missing.");
        AssertTrue(chatBody.Contains("\"role\":\"tool\"", StringComparison.Ordinal), "Chat tool role missing.");
        AssertTrue(chatBody.Contains("\"tool_call_id\":\"call_1\"", StringComparison.Ordinal), "Chat tool_call_id missing.");
        AssertTrue(!chatBody.Contains("tool lookup result:", StringComparison.Ordinal), "Generic tool-result text used as protocol continuation.");
    }

    private static void AnthropicStreamMergesMessageStartUsage()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(
                "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":11,\"cache_read_input_tokens\":4}}}\n\n" +
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        var adapter = new AnthropicMessagesAdapter(new ProviderHttpTransport(handler));
        var definition = ProviderDefinitionFactory.Create("p", ProtocolKind.AnthropicMessages, "https://api.anthropic.com", "cred", "m", allowInsecureLocalHttp: false);
        using var secret = new ResolvedProviderSecret("token");
        var events = Drain(adapter.StreamAsync(
            definition,
            ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!,
            secret,
            new ProviderInvokeRequest(SampleIr(), new ModelId("m"), true, new Dictionary<string, string>()),
            CancellationToken.None));
        var usage = events.Last(item => item.Usage is not null).Usage!;
        AssertEqual(11L, usage.InputTokens.Value.GetValueOrDefault(), "message_start input usage lost.");
        AssertEqual(4L, usage.CachedInputReadTokens.Value.GetValueOrDefault(), "message_start cache usage lost.");
        AssertEqual(3L, usage.OutputTokens.Value.GetValueOrDefault(), "message_delta output usage lost.");
        AssertTrue(usage.ReasoningTokens.Value is null, "Missing reasoning tokens became zero.");
    }

    private static void CompatibleChatThinkingWithToolsHonorsCapability()
    {
        var adapter = new OpenAiCompatibleChatAdapter(new ProviderHttpTransport(new ScriptedHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var definition = ProviderDefinitionFactory.Create(
            "p", ProtocolKind.OpenAiCompatibleChatCompletions, "https://api.example.com", "cred", "m",
            allowInsecureLocalHttp: false, extensions: new Dictionary<string, string> { ["thinking"] = "enabled" });
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, false, out _)!;
        var certified = CertificationFactory.Certified(
            definition.ProviderDefinitionId, definition.Revision, definition.Endpoint,
            adapter.AdapterId, adapter.AdapterVersion, new ModelId("m"),
            (ProtocolCapabilityNames.ThinkingWithTools, CapabilitySupport.Supported));
        var allowed = adapter.Prepare(definition, endpoint, new ProviderInvokeRequest(
            SampleIr(tools: true), new ModelId("m"), false, definition.AdapterExtensions, "inv", ProtocolProfile: certified));
        AssertTrue(allowed.Succeeded, "Certified thinking+tools continuation was hard-denied.");
    }

    private static ModelCertificationRecord AnthropicThinkingProfile() =>
        CertificationFactory.Certified(
            new ProviderDefinitionId("p"), new ProviderRevision(1), "https://api.anthropic.com/",
            "anthropic_messages", AnthropicMessagesAdapter.CurrentVersion, new ModelId("m"),
            (ProtocolCapabilityNames.ThinkingManual, CapabilitySupport.Supported),
            (ProtocolCapabilityNames.ThinkingAdaptive, CapabilitySupport.Supported),
            (ProtocolCapabilityNames.ThinkingEffort, CapabilitySupport.Supported));

    private static List<ModelRuntimeEvent> Drain(IAsyncEnumerable<ModelRuntimeEvent> stream)
    {
        var events = new List<ModelRuntimeEvent>();
        var enumerator = stream.GetAsyncEnumerator();
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

        return events;
    }

    private static ProviderHttpResult Json(int status, string body) =>
        new((InvocationLifecycle)0, InvocationFailureClass.None, status, "req", Encoding.UTF8.GetBytes(body), "application/json", "redacted", false);

    private static string Wp14Canary() => "sk-wp14-canary-9f3a2c";
}
