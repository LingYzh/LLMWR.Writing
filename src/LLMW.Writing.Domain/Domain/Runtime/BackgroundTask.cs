using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Runtime;

public enum BackgroundTaskKind
{
    SubAgentRun,
    ToolCall,
    Worker,
    RuntimeTask
}

public enum BackgroundTaskStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Interrupted,
    Cancelled
}

public enum BackgroundRecoveryClassification
{
    Completed,
    ResumableInterrupted,
    OwnerCancelled,
    WorkerOrToolGone,
    UnknownSideEffect,
    CheckpointAvailable,
    StillQueued
}

public sealed record BackgroundExecutionRef(
    BackgroundTaskKind Kind,
    string? RunId,
    string? ToolCallId,
    string? WorkerInstanceId,
    string? TaskId);

public sealed record DurableBackgroundTaskRecord(
    string BackgroundTaskId,
    string OwnerRunId,
    string? OwnerTaskId,
    string KindJson,
    string Status,
    string? CheckpointId,
    long StartedAtMs,
    long? CompletedAtMs);

public static class BackgroundTaskKindCodec
{
    public static string ToDurableValue(BackgroundTaskKind kind) => kind switch
    {
        BackgroundTaskKind.SubAgentRun => "sub_agent_run",
        BackgroundTaskKind.ToolCall => "tool_call",
        BackgroundTaskKind.Worker => "worker",
        BackgroundTaskKind.RuntimeTask => "runtime_task",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out BackgroundTaskKind kind)
    {
        kind = value switch
        {
            "sub_agent_run" => BackgroundTaskKind.SubAgentRun,
            "tool_call" => BackgroundTaskKind.ToolCall,
            "worker" => BackgroundTaskKind.Worker,
            "runtime_task" => BackgroundTaskKind.RuntimeTask,
            _ => default
        };
        return value is "sub_agent_run" or "tool_call" or "worker" or "runtime_task";
    }
}

public static class BackgroundTaskStatusCodec
{
    public static string ToDurableValue(BackgroundTaskStatus status) => status switch
    {
        BackgroundTaskStatus.Queued => "queued",
        BackgroundTaskStatus.Running => "running",
        BackgroundTaskStatus.Paused => "paused",
        BackgroundTaskStatus.Completed => "completed",
        BackgroundTaskStatus.Failed => "failed",
        BackgroundTaskStatus.Interrupted => "interrupted",
        BackgroundTaskStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string? value, out BackgroundTaskStatus status)
    {
        status = value switch
        {
            "queued" => BackgroundTaskStatus.Queued,
            "running" => BackgroundTaskStatus.Running,
            "paused" => BackgroundTaskStatus.Paused,
            "completed" => BackgroundTaskStatus.Completed,
            "failed" => BackgroundTaskStatus.Failed,
            "interrupted" => BackgroundTaskStatus.Interrupted,
            "cancelled" => BackgroundTaskStatus.Cancelled,
            _ => default
        };
        return value is "queued" or "running" or "paused" or "completed" or "failed" or "interrupted" or "cancelled";
    }
}

public static class BackgroundExecutionRefCodec
{
    public const int SchemaVersion = 1;

    public static string WriteKindColumn(BackgroundExecutionRef execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("kind", BackgroundTaskKindCodec.ToDurableValue(execution.Kind));
            writer.WritePropertyName("execution");
            writer.WriteStartObject();
            writer.WriteString("type", execution.Kind switch
            {
                BackgroundTaskKind.SubAgentRun => "subAgentRun",
                BackgroundTaskKind.ToolCall => "toolCall",
                BackgroundTaskKind.Worker => "worker",
                BackgroundTaskKind.RuntimeTask => "runtimeTask",
                _ => throw new ArgumentOutOfRangeException(nameof(execution), execution.Kind, null)
            });
            WriteOptional(writer, "runId", execution.RunId);
            WriteOptional(writer, "toolCallId", execution.ToolCallId);
            WriteOptional(writer, "workerInstanceId", execution.WorkerInstanceId);
            WriteOptional(writer, "taskId", execution.TaskId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static BackgroundExecutionRef ParseKindColumn(string kindJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kindJson);
        if (kindJson.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(kindJson);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidOperationException("Unsupported background execution schemaVersion.");
            }

            if (!BackgroundTaskKindCodec.TryParse(root.GetProperty("kind").GetString(), out var kind))
            {
                throw new InvalidOperationException("Unsupported background task kind.");
            }

            var execution = root.GetProperty("execution");
            return new BackgroundExecutionRef(
                kind,
                Optional(execution, "runId"),
                Optional(execution, "toolCallId"),
                Optional(execution, "workerInstanceId"),
                Optional(execution, "taskId"));
        }

        if (!BackgroundTaskKindCodec.TryParse(kindJson, out var legacy))
        {
            throw new InvalidOperationException("Unsupported background task kind.");
        }

        return new BackgroundExecutionRef(legacy, null, null, null, null);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}

public static class BackgroundTaskLifecycle
{
    public static bool IsLegal(BackgroundTaskStatus current, BackgroundTaskStatus next) => (current, next) switch
    {
        (BackgroundTaskStatus.Queued, BackgroundTaskStatus.Running) => true,
        (BackgroundTaskStatus.Queued, BackgroundTaskStatus.Cancelled) => true,
        (BackgroundTaskStatus.Running, BackgroundTaskStatus.Paused) => true,
        (BackgroundTaskStatus.Running, BackgroundTaskStatus.Completed) => true,
        (BackgroundTaskStatus.Running, BackgroundTaskStatus.Failed) => true,
        (BackgroundTaskStatus.Running, BackgroundTaskStatus.Interrupted) => true,
        (BackgroundTaskStatus.Running, BackgroundTaskStatus.Cancelled) => true,
        (BackgroundTaskStatus.Paused, BackgroundTaskStatus.Running) => true,
        (BackgroundTaskStatus.Paused, BackgroundTaskStatus.Cancelled) => true,
        (BackgroundTaskStatus.Paused, BackgroundTaskStatus.Failed) => true,
        (BackgroundTaskStatus.Interrupted, BackgroundTaskStatus.Running) => true,
        (BackgroundTaskStatus.Interrupted, BackgroundTaskStatus.Cancelled) => true,
        (BackgroundTaskStatus.Interrupted, BackgroundTaskStatus.Failed) => true,
        _ => false
    };

    public static long? DurationMs(DurableBackgroundTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.CompletedAtMs is long completed ? completed - record.StartedAtMs : null;
    }

    public static BackgroundRecoveryClassification ClassifyRestart(
        DurableBackgroundTaskRecord record,
        bool ownerCancelled,
        bool workerOrToolAlive,
        bool unknownSideEffect,
        bool checkpointAvailable)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!BackgroundTaskStatusCodec.TryParse(record.Status, out var status))
        {
            return BackgroundRecoveryClassification.WorkerOrToolGone;
        }

        if (status == BackgroundTaskStatus.Completed)
        {
            return BackgroundRecoveryClassification.Completed;
        }

        if (ownerCancelled || status == BackgroundTaskStatus.Cancelled)
        {
            return BackgroundRecoveryClassification.OwnerCancelled;
        }

        if (unknownSideEffect)
        {
            return BackgroundRecoveryClassification.UnknownSideEffect;
        }

        if (status == BackgroundTaskStatus.Queued)
        {
            return BackgroundRecoveryClassification.StillQueued;
        }

        if (status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Paused)
        {
            if (!workerOrToolAlive)
            {
                return BackgroundRecoveryClassification.WorkerOrToolGone;
            }

            return checkpointAvailable
                ? BackgroundRecoveryClassification.CheckpointAvailable
                : BackgroundRecoveryClassification.ResumableInterrupted;
        }

        if (status == BackgroundTaskStatus.Interrupted)
        {
            return checkpointAvailable
                ? BackgroundRecoveryClassification.CheckpointAvailable
                : BackgroundRecoveryClassification.ResumableInterrupted;
        }

        return BackgroundRecoveryClassification.WorkerOrToolGone;
    }
}

public sealed record EvidenceRecord(
    string EvidenceId,
    string? RunId,
    string? TaskId,
    string SourceKind,
    string SourceId,
    string SourceDigest,
    string LocatorJson,
    bool Stale,
    long CreatedAtMs);

public static class EvidenceFreshness
{
    public static bool IsStale(string storedDigest, string currentSourceDigest) =>
        !StringComparer.Ordinal.Equals(storedDigest, currentSourceDigest);
}
