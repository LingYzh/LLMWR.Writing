# WP14 Provider Protocol Research

**Research date**: 2026-08-16  
**Status**: implementation baseline for WP14 adapters  
**Does not add NuGet SDKs.** Wire mapping uses `HttpClient` + `System.Text.Json`.

## Official sources verified

| Protocol family | Official source | Notes verified |
|---|---|---|
| OpenAI Responses | https://developers.openai.com/api/docs/guides/migrate-to-responses | Typed `input`/`output` items; `text.format` for structured output; function calls use `call_id`; streaming is typed SSE events, not Chat Completions `delta` chunks. |
| OpenAI Responses streaming | https://developers.openai.com/api/reference/resources/responses/streaming-events/ | Lifecycle includes `response.created`, `response.output_text.delta`, `response.function_call_arguments.delta` / `.done`, `response.incomplete`, `response.completed`, `response.failed`. EOF is not completion. |
| OpenAI Responses create | https://developers.openai.com/api/docs/api-reference/responses/create | `store` is an explicit request field. WP14 sets `store=false` and does not use `previous_response_id` for Runtime recovery. |
| OpenAI function calling | https://developers.openai.com/api/docs/guides/function-calling | Tool proposal is model output; application executes locally. Hosted tools (web search, code interpreter, computer use, MCP) are not enabled. |
| Anthropic Messages | https://platform.claude.com/docs/en/build-with-claude/working-with-messages | Top-level `system` is not an input `role:system` message. Client tools return `tool_use`; results return as `tool_result`. Server tools are not enabled. |
| Anthropic prompt caching / usage | https://platform.claude.com/docs/en/build-with-claude/prompt-caching | Usage may include `cache_creation_input_tokens` and `cache_read_input_tokens`. Missing usage is Unknown, not zero. |
| Anthropic tool streaming | https://platform.claude.com/docs/en/agents-and-tools/tool-use/fine-grained-tool-streaming | `content_block_start` + `input_json_delta` (`partial_json`); assemble only after block stop. |
| DeepSeek OpenAI-compatible | https://api-docs.deepseek.com/ | Base URL `https://api.deepseek.com`. Current documented aliases include `deepseek-v4-flash` and `deepseek-v4-pro`. Model names are catalog data, not Domain constants. |
| DeepSeek Anthropic-compatible | https://api-docs.deepseek.com/guides/anthropic_api | Base URL `https://api.deepseek.com/anthropic`. Compatibility ≠ Anthropic semantic identity. Unsupported fields must not become capability truth. |
| DeepSeek thinking | https://api-docs.deepseek.com/guides/thinking_mode | Chat Completions: `thinking.type` enabled/disabled + `reasoning_effort`. Responses: `reasoning.effort`. Raw `reasoning_content` is hidden CoT, not a Result Artifact. |
| DeepSeek Chat Completions | https://api-docs.deepseek.com/api/create-chat-completion | Usage includes `prompt_cache_hit_tokens` / `prompt_cache_miss_tokens` and optional `reasoning_tokens`. |
| DeepSeek pricing page | https://api-docs.deepseek.com/quick_start/pricing | Prices change; WP14 never hardcodes them as billing truth. Historical invocations freeze a price snapshot reference. |

## Protocol assumptions implemented

1. **OpenAI-compatible ≠ OpenAI Responses.** Chat Completions adapters certify extra fields separately.
2. **Anthropic-compatible custom endpoints** use the Anthropic Messages adapter with a configured endpoint; business logic never branches on vendor display name.
3. **DeepSeek** is a compatibility provider: ProtocolKind + endpoint + allow-listed adapter extensions (`thinking`, `reasoningEffort`). No `if provider.Name == "DeepSeek"` in Domain/Application routing.
4. **`store=false`** on OpenAI Responses. Provider-stored conversations are not Runtime checkpoints.
5. **API version header** for Anthropic Messages: `anthropic-version: 2023-06-01` (current documented Messages version at research date). Missing/unknown version is a protocol configuration fact, not guessed per model name.
6. **Client `InvocationId`** is generated before dispatch. Provider `x-request-id` / response `id` are captured separately and are not idempotency keys.
7. **No vendor SDK packages.**

## Process ownership (frozen Architecture §1.3 + §29)

| Fact | Owner process | Durable store |
|---|---|---|
| Provider Definition / routing / catalog / certification | Agent Runtime | Application-level JSON (not `project.db`) |
| Credential value | Agent Runtime credential host at HTTP send | Windows Credential Manager / in-memory test resolver |
| Provider HTTP | Agent Runtime | none (wire is transient) |
| Prompt compilation / routing / invocation snapshot freeze | Agent Runtime | snapshot metadata persisted via Core `persistCheckpoint` + `runs` identity columns |
| `project.db` | Authority Core only writer | schema v1 as-is |
| Worker | restricted tools | must not receive Provider secrets |

No new Core IPC methods in WP14: credentials must not cross Core. UI configuration of Providers is WP15.
