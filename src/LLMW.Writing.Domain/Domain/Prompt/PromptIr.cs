namespace LLMW.Writing.Domain.Prompt;

public static class PromptCompilerVersions
{
    public const string Current = "wp14-prompt-compiler-v1";
}

public enum PromptLayer
{
    RuntimePolicy = 0,
    BaseRole = 1,
    Behavioral = 2,
    ContentOverlay = 3,
    ProjectContext = 4,
    Skills = 5,
    Workflow = 6,
    Task = 7,
    User = 8
}

public enum PromptSemanticRole
{
    Instruction,
    Context,
    Evidence,
    Result,
    UserText,
    ToolSchema,
    OutputContract
}

public enum PromptSourceKind
{
    RuntimeKernel,
    BaseRole,
    ShippedBehavioral,
    UserBehavioralOverride,
    ContentMode,
    ProjectInstructions,
    Skill,
    Workflow,
    TaskContract,
    RequiredResult,
    AdvisoryResult,
    Narrative,
    UserRequest,
    ToolSchema,
    OutputContract
}

public enum PromptTrustClass
{
    RuntimeEnforced,
    ApplicationConfigured,
    ProjectConfigured,
    UntrustedContext
}

public enum PromptSensitivityClass
{
    None,
    SecretAdjacent,
    UserPrivate
}

public enum PromptTruncationClass
{
    Never,
    PreferKeep,
    Advisory,
    Optional,
    Historical
}

public enum ContentBehaviorMode
{
    Unspecified,
    Sfw,
    Nsfw
}

public enum BehavioralOverrideMode
{
    Default,
    Replace,
    Append
}

public sealed record PromptBlock(
    string BlockId,
    PromptLayer Layer,
    PromptSemanticRole SemanticRole,
    PromptSourceKind SourceKind,
    string? SourceId,
    string SourceDigest,
    string Content,
    bool Mandatory,
    PromptTrustClass TrustClass,
    PromptSensitivityClass SensitivityClass,
    PromptTruncationClass TruncationClass,
    string? CacheHint,
    string Provenance);

public sealed record AuthorizedToolSchema(
    string ToolName,
    string Description,
    string ParametersJson,
    IReadOnlyList<string> RequiredProperties);

public enum OutputContractKind
{
    PlainText,
    StructuredJson,
    ToolCalling
}

public sealed record PromptOutputContract(
    OutputContractKind Kind,
    string? SchemaJson,
    IReadOnlyList<string> RequiredProperties);

public sealed record PromptIr(
    string CompilerVersion,
    IReadOnlyList<PromptBlock> Blocks,
    IReadOnlyList<AuthorizedToolSchema> Tools,
    PromptOutputContract OutputContract,
    string GenerationIntent,
    int? ContextBudgetTokens,
    int ReservedOutputTokens,
    string PromptConfigId,
    string EffectivePromptDigest)
{
    public IReadOnlyList<PromptBlock> OrderedBlocks =>
        Blocks.OrderBy(block => (int)block.Layer).ThenBy(block => block.BlockId, StringComparer.Ordinal).ToArray();
}

public sealed record PromptCompileFailure(string Code, string Message);

public sealed record PromptCompileResult(PromptIr? Ir, PromptCompileFailure? Failure)
{
    public bool Succeeded => Failure is null && Ir is not null;
}

public sealed record PromptCompileRequest(
    string CompilerVersion,
    string BaseRoleId,
    string BaseRoleContract,
    string ShippedBehavioralPrompt,
    BehavioralOverrideMode OverrideMode,
    string? UserBehavioralOverride,
    ContentBehaviorMode ContentMode,
    IReadOnlyList<string> ProjectInstructionTexts,
    IReadOnlyList<(string SkillId, string Text)> Skills,
    string? WorkflowContext,
    string TaskContract,
    IReadOnlyList<(string ResultId, string Text, bool Required, bool Stale)> Results,
    IReadOnlyList<(string SourceId, string Text)> NarrativeContext,
    string? UserRequest,
    IReadOnlyList<AuthorizedToolSchema> AuthorizedTools,
    PromptOutputContract OutputContract,
    int? ContextBudgetTokens,
    int ReservedOutputTokens);
