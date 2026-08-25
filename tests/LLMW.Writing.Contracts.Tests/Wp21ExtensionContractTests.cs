using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp21ExtensionContractTests
{
    public static int Run()
    {
        const string operationId = "018f3e78-1234-7abc-8def-0123456789ae";
        var activate = Program.Envelope(
            IpcMessageType.Request,
            IpcSemanticTypes.ActivateExtension,
            new ActivateExtensionRequest("mcp:research", operationId));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(activate, IpcJsonContext.Default.ActivateExtensionRequestEnvelope));
        Program.AssertTrue(json.Contains("\"extensionId\":\"mcp:research\"", StringComparison.Ordinal) &&
                           json.Contains("\"operationId\":\"" + operationId + "\"", StringComparison.Ordinal),
            "WP21 activation request did not use typed extension and operation identities.");
        Program.AssertTrue(!json.Contains("path", StringComparison.OrdinalIgnoreCase) &&
                           !json.Contains("script", StringComparison.OrdinalIgnoreCase) &&
                           !json.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                           !json.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "WP21 activation IPC exposed a filesystem, executable, credential, or secret surface.");
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.TrustProjectExtensions) &&
                           IpcSemanticTypes.IsKnown(IpcSemanticTypes.RevokeProjectExtensionsTrust) &&
                           IpcSemanticTypes.IsKnown(IpcSemanticTypes.DeactivateExtension) &&
                           IpcSemanticTypes.IsKnown(IpcSemanticTypes.ListExtensions),
            "WP21 extension semantic types were not registered.");
        Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.ActivateExtension) &&
                           !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.TrustProjectExtensions),
            "Trust and activation mutations must not be transport replay-safe.");

        var response = Program.Envelope(
            IpcMessageType.Response,
            IpcSemanticTypes.ListExtensions,
            new ListExtensionsResponse(
                true,
                [new ExtensionStatusResponse("skill:writer", "skill", "project", "1.0.0", true, false)],
                ["AGENTS_CLAUDE_CONFLICT"]));
        var roundTrip = IpcJson.Deserialize(
            IpcJson.Serialize(response, IpcJsonContext.Default.ListExtensionsResponseEnvelope),
            IpcJsonContext.Default.ListExtensionsResponseEnvelope);
        Program.AssertEqual("skill:writer", roundTrip.Payload.Extensions.Single().ExtensionId,
            "WP21 status response did not preserve the path-free extension identity.");
        Console.WriteLine("WP21 extension contract tests passed (5).");
        return 5;
    }
}
