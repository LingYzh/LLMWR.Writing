using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp20ProjectPackageContractTests
{
    public static int Run()
    {
        const string operationId = "018f3e78-1234-7abc-8def-0123456789ae";
        var backup = Program.Envelope(
            IpcMessageType.Request,
            IpcSemanticTypes.CreateProjectBackup,
            new CreateProjectBackupRequest(operationId));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(backup, IpcJsonContext.Default.CreateProjectBackupRequestEnvelope));
        Program.AssertTrue(json.Contains("\"operationId\":\"" + operationId + "\"", StringComparison.Ordinal),
            "WP20 backup request did not round-trip through source-generated metadata.");
        Program.AssertTrue(!json.Contains("path", StringComparison.OrdinalIgnoreCase) &&
                           !json.Contains("destination", StringComparison.OrdinalIgnoreCase),
            "WP20 contract exposed a project or destination path.");
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.CreateProjectArchive) &&
                           IpcSemanticTypes.IsKnown(IpcSemanticTypes.CreateFinalPackage) &&
                           IpcSemanticTypes.IsKnown(IpcSemanticTypes.VerifyFinalPackage),
            "WP20 semantic types were not registered.");
        Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.CreateProjectBackup) &&
                           !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.CreateProjectArchive) &&
                           !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.CreateFinalPackage),
            "Package mutations must not be transport replay-safe.");

        var verify = Program.Envelope(
            IpcMessageType.Response,
            IpcSemanticTypes.VerifyFinalPackage,
            new VerifyFinalPackageResponse(operationId, "verified", null));
        var response = IpcJson.Deserialize(
            IpcJson.Serialize(verify, IpcJsonContext.Default.VerifyFinalPackageResponseEnvelope),
            IpcJsonContext.Default.VerifyFinalPackageResponseEnvelope);
        Program.AssertEqual("verified", response.Payload.Status, "Final package verification response did not round-trip.");
        Console.WriteLine("WP20 project package contract tests passed (5).");
        return 5;
    }
}
