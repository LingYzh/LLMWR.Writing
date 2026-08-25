using System.Text.Json;
using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.Extensions;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string Wp21ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";

    private static void RunWp21Tests()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP21.Integration", Guid.NewGuid().ToString("N"));
        try
        {
            var appExtensions = Path.Combine(root, "application");
            var userExtensions = Path.Combine(root, "user");
            var projectRoot = Path.Combine(root, "project");
            var projectExtensions = Path.Combine(projectRoot, "Extensions");
            var stateRoot = Path.Combine(root, "state");
            var writer = Path.Combine(projectExtensions, "writer");
            Directory.CreateDirectory(Path.Combine(writer, "scripts"));
            Directory.CreateDirectory(appExtensions);
            Directory.CreateDirectory(userExtensions);
            Directory.CreateDirectory(stateRoot);
            File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "Project writing rules");
            File.WriteAllText(Path.Combine(writer, "extension.llmw.json"),
                "{\"kind\":\"skill\",\"name\":\"writer\",\"version\":\"1.0.0\",\"description\":\"safe\",\"instructions\":\"Skill writing rules\",\"scripts\":[\"scripts/check.ps1\"],\"requestedPermissions\":[\"Script.Execute\"],\"dependencies\":[]}");
            var scriptPath = Path.Combine(writer, "scripts", "check.ps1");
            File.WriteAllText(scriptPath, "Write-Output 'never executed'");

            var catalog = new FileExtensionCatalog(new ExtensionCatalogRoots(
                appExtensions, userExtensions, projectExtensions, projectRoot));
            var holder = new ExtensionActivationServiceHolder();
            var service = new ExtensionActivationService(
                catalog,
                new FileExtensionSecurityStateStore(stateRoot, Wp21ProjectId, projectRoot),
                Wp21ProjectId);
            holder.PublishOnce(service);
            var handler = new Wp21IpcCommandHandler(holder, "wp21-integration");
            var principal = new TrustedNativePrincipalSource("wp21-integration").ResolveUserInteractive();

            var trust = HandleExtension(handler, principal, IpcSemanticTypes.TrustProjectExtensions,
                "{\"operationId\":\"018f3e78-1234-7abc-8def-0123456789ae\"}");
            AssertTrue(IpcJson.Deserialize(trust, IpcJsonContext.Default.TrustProjectExtensionsResponseEnvelope).Payload.ProjectTrusted,
                "Typed IPC trust command did not reach Application/Infrastructure.");
            var activation = HandleExtension(handler, principal, IpcSemanticTypes.ActivateExtension,
                "{\"extensionId\":\"skill:writer\",\"operationId\":\"018f3e78-1234-7abc-8def-0123456789af\"}");
            AssertTrue(IpcJson.Deserialize(activation, IpcJsonContext.Default.ActivateExtensionResponseEnvelope).Payload.Activated,
                "Typed IPC activation did not complete through the Domain state transition and file-backed state store.");
            var inputs = service.GetPromptInputs();
            AssertEqual("Project writing rules", inputs.ProjectInstructions.Single(),
                "AGENTS content was not carried through the Core-owned extension source.");
            AssertEqual("writer", inputs.Skills.Single().SkillId,
                "Activated project Skill did not enter the deterministic prompt input set.");

            File.WriteAllText(scriptPath, "Write-Output 'changed but never executed'");
            var listing = HandleExtension(handler, principal, IpcSemanticTypes.ListExtensions, "{}");
            var status = IpcJson.Deserialize(listing, IpcJsonContext.Default.ListExtensionsResponseEnvelope).Payload.Extensions.Single();
            AssertTrue(!status.Activated && status.Invalidated,
                "Changed extension executable content did not invalidate activation before any execution path.");
            AssertTrue(service.GetPromptInputs().Skills.Count == 0,
                "Invalidated extension continued contributing prompts after hash change.");
            Console.WriteLine("WP21 IPC → Application → Domain → Infrastructure integration test passed.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] HandleExtension(
        Wp21IpcCommandHandler handler,
        CallerPrincipal principal,
        string semanticType,
        string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
                IpcClientKind.Ui,
                "wp21-connection",
                null,
                principal,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.Parse(Wp21ProjectId),
                null,
                semanticType,
                document.RootElement.Clone(),
                CancellationToken.None))
            .GetAwaiter().GetResult();
        return result?.ResponseUtf8 ?? throw new InvalidOperationException("WP21 IPC command did not return a response.");
    }
}
