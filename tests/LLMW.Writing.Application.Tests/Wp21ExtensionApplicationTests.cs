using System.Text.Json;
using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Extensions;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Wp21ExtensionApplicationTests
{
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";
    private const string TrustOperationId = "018f3e78-1234-7abc-8def-0123456789ae";
    private const string ActivateOperationId = "018f3e78-1234-7abc-8def-0123456789af";

    public static int Run()
    {
        var count = 0;
        count += TrustIsSeparateFromActivationAndReplayIsIdempotent();
        count += IpcRequiresTrustedUiAndMatchingProjectBinding();
        return count;
    }

    private static int TrustIsSeparateFromActivationAndReplayIsIdempotent()
    {
        var catalog = new FakeCatalog();
        var state = new FakeStateStore();
        var service = new ExtensionActivationService(catalog, state, ProjectId);
        var user = new TrustedNativePrincipalSource("wp21-app").ResolveUserInteractive();
        var activation = new ActivateExtensionCommand(ProjectId, "skill:writer", ActivateOperationId);

        AssertEqual(ExtensionFailureCode.ProjectTrustRequired, service.Activate(user, activation).Failure!.Code,
            "Project content was allowed to activate before a separate Project Trust decision.");
        AssertEqual(0, state.SaveCalls, "Trust denial must not persist an activation.");

        var trusted = service.TrustProject(user, new ExtensionOperationRequest(ProjectId, TrustOperationId));
        AssertTrue(trusted.Succeeded && trusted.Value!.ProjectTrusted, "Explicit Project Trust was not recorded.");
        var first = service.Activate(user, activation);
        var replay = service.Activate(user, activation);
        AssertTrue(first.Succeeded && replay.Succeeded && first.Value!.Activated && replay.Value!.Activated,
            "Exact activation replay was not idempotent.");
        AssertEqual(2, state.Operations.Count, "Replay created a second mutation record.");
        AssertEqual(ExtensionFailureCode.OperationIdentityConflict,
            service.Activate(user, activation with { ExtensionId = "mcp:research" }).Failure!.Code,
            "Operation identity reuse with different input was not rejected.");

        catalog.WriterDigest = Digest('b');
        var listed = service.List();
        var writer = listed.Extensions.Single(item => item.ExtensionId == "skill:writer");
        AssertTrue(!writer.Activated && writer.Invalidated,
            "Script/config digest change did not invalidate the prior activation.");
        AssertTrue(service.GetCurrent().SkillDigests.Count == 0,
            "Invalidated Skill leaked into prompt/freshness inputs.");
        return 9;
    }

    private static int IpcRequiresTrustedUiAndMatchingProjectBinding()
    {
        var catalog = new FakeCatalog();
        var state = new FakeStateStore();
        var holder = new ExtensionActivationServiceHolder();
        holder.PublishOnce(new ExtensionActivationService(catalog, state, ProjectId));
        var handler = new Wp21IpcCommandHandler(holder, "workspace-21");
        var runtime = Handle(handler, IpcClientKind.AgentRuntime, null, Guid.Parse(ProjectId),
            IpcSemanticTypes.TrustProjectExtensions, "{\"operationId\":\"" + TrustOperationId + "\"}");
        AssertEqual(IpcErrorCodes.ExtensionMutationDenied, Error(runtime).Code,
            "Runtime caller reached the Project Trust mutation surface.");
        var user = new TrustedNativePrincipalSource("wp21-ipc").ResolveUserInteractive();
        var wrongProject = Handle(handler, IpcClientKind.Ui, user,
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789b0"), IpcSemanticTypes.TrustProjectExtensions,
            "{\"operationId\":\"" + TrustOperationId + "\"}");
        AssertEqual(IpcErrorCodes.BindingMismatch, Error(wrongProject).Code,
            "Cross-project extension mutation reached Application.");
        AssertEqual(0, state.SaveCalls, "Rejected IPC requests reached the activation store.");
        return 3;
    }

    private static byte[] Handle(
        Wp21IpcCommandHandler handler,
        IpcClientKind kind,
        CallerPrincipal? principal,
        Guid projectId,
        string semanticType,
        string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
                kind,
                "connection-21",
                null,
                principal,
                Guid.NewGuid(),
                Guid.NewGuid(),
                projectId,
                null,
                semanticType,
                document.RootElement.Clone(),
                CancellationToken.None))
            .GetAwaiter().GetResult();
        return result?.ResponseUtf8 ?? throw new InvalidOperationException("WP21 handler did not return a response.");
    }

    private static IpcError Error(byte[] response) =>
        IpcJson.Deserialize(response, IpcJsonContext.Default.ErrorEnvelope).Payload;

    private static string Digest(char value) => new(value, 64);

    private static ExtensionDescriptor Descriptor(string id, string digest)
    {
        var parts = id.Split(':', 2);
        var kind = parts[0] == "mcp" ? ExtensionKind.McpServer : ExtensionKind.Skill;
        return new ExtensionDescriptor(new ExtensionManifest(
            kind,
            parts[1],
            "1.0.0",
            "safe",
            "instruction " + parts[1],
            [],
            [Capability.McpCall],
            []), ExtensionScope.Project, digest);
    }

    private sealed class FakeCatalog : IExtensionCatalog
    {
        public string WriterDigest { get; set; } = Digest('a');

        public ExtensionCatalogSnapshot Discover(string relativeProjectPath = "") => new(
            ExtensionCatalogResolver.Resolve([Descriptor("skill:writer", WriterDigest), Descriptor("mcp:research", Digest('c'))]),
            new ProjectInstructionSnapshot(Digest('d'), ["root instructions"], []));
    }

    private sealed class FakeStateStore : IExtensionSecurityStateStore
    {
        public ExtensionSecurityState State { get; private set; } = ExtensionSecurityState.Empty;

        public int SaveCalls { get; private set; }

        public IReadOnlyDictionary<string, ExtensionOperationReceipt> Operations => State.Operations;

        public ExtensionSecurityState Load() => State;

        public void Save(ExtensionSecurityState state)
        {
            State = state;
            SaveCalls++;
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}
