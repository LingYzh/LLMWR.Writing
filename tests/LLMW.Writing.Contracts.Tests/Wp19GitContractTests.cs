using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp19GitContractTests
{
    public static void Run()
    {
        GitSemanticTypesAreKnownAndNeverReplayable();
        GitContractsAreTypedAndDoNotExposePathsOrCommands();
        CommitRequestGoldenJsonIsNarrow();
        Console.WriteLine("WP19 Git contract tests passed.");
    }

    private static void GitSemanticTypesAreKnownAndNeverReplayable()
    {
        foreach (var semanticType in GitTypes())
        {
            Program.AssertTrue(IpcSemanticTypes.IsKnown(semanticType), semanticType + " must be a known semantic type.");
            Program.AssertTrue(IpcSemanticTypes.IsWellFormed(semanticType, IpcMessageType.Request), semanticType + " must allow a request.");
            Program.AssertTrue(IpcSemanticTypes.IsWellFormed(semanticType, IpcMessageType.Response), semanticType + " must allow a response.");
            Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(semanticType), semanticType + " must not replay after reconnect.");
        }
    }

    private static void GitContractsAreTypedAndDoNotExposePathsOrCommands()
    {
        var payloadTypes = new[]
        {
            typeof(GetGitStatusRequest),
            typeof(GetGitDiffSummaryRequest),
            typeof(GetGitCurrentBranchRequest),
            typeof(ListGitCommitHistoryRequest),
            typeof(GetGitCommitMetadataRequest),
            typeof(CommitGitChangesRequest)
        };
        foreach (var property in payloadTypes.SelectMany(type => type.GetProperties()))
        {
            Program.AssertTrue(!property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase),
                "Git IPC exposed a caller-selected path: " + property.Name);
            Program.AssertTrue(!property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase) &&
                               !property.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase) &&
                               !property.Name.Contains("Executable", StringComparison.OrdinalIgnoreCase),
                "Git IPC exposed a generic execution field: " + property.Name);
        }

        foreach (var semanticType in IpcSemanticTypes.All)
        {
            Program.AssertTrue(semanticType is not "executeGit" and not "git.execute" and not "git.invoke",
                "Generic Git RPC must not exist: " + semanticType);
        }
    }

    private static void CommitRequestGoldenJsonIsNarrow()
    {
        var envelope = Program.Envelope(
            IpcMessageType.Request,
            IpcSemanticTypes.CommitGitChanges,
            new CommitGitChangesRequest("User-approved commit", true));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, IpcJsonContext.Default.CommitGitChangesRequestEnvelope));
        const string expected = "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"commitGitChanges\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"message\":\"User-approved commit\",\"stageAll\":true}}";
        Program.AssertEqual(expected, json, "WP19 typed commit request changed.");
    }

    private static string[] GitTypes() =>
    [
        IpcSemanticTypes.GetGitStatus,
        IpcSemanticTypes.GetGitDiffSummary,
        IpcSemanticTypes.GetGitCurrentBranch,
        IpcSemanticTypes.ListGitCommitHistory,
        IpcSemanticTypes.GetGitCommitMetadata,
        IpcSemanticTypes.CommitGitChanges
    ];
}
