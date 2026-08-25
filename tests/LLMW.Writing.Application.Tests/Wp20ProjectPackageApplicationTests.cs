using System.Text.Json;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.ProjectPackages;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Tests;

internal static class Wp20ProjectPackageApplicationTests
{
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";
    private const string OperationId = "018f3e78-1234-7abc-8def-0123456789ae";

    public static int Run()
    {
        var count = 0;
        count += MutationRequiresTrustedBoundUserAndValidRequest();
        count += ReplayIsIdempotentAndIdentityConflictStopsStorage();
        count += RuntimeAndCrossProjectIpcCannotReachPackageStore();
        return count;
    }

    private static int MutationRequiresTrustedBoundUserAndValidRequest()
    {
        var store = new FakeStore();
        var service = new ProjectPackageService(store, ProjectId);
        var request = new ProjectPackageRequest(ProjectPackageKind.Backup, ProjectId, OperationId);
        var user = new TrustedNativePrincipalSource("wp20-app").ResolveUserInteractive();

        AssertEqual(ProjectPackageFailureCode.MutationDenied,
            service.Create(null, true, request).Failure!.Code,
            "Unauthenticated package creation must be denied.");
        AssertEqual(ProjectPackageFailureCode.MutationDenied,
            service.Create(user, false, request).Failure!.Code,
            "Implicit package creation must be denied.");
        AssertEqual(ProjectPackageFailureCode.ProjectBindingInvalid,
            service.Create(user, true, request with { ProjectId = "018f3e78-1234-7abc-8def-0123456789af" }).Failure!.Code,
            "Cross-project package creation must be denied.");
        AssertEqual(ProjectPackageFailureCode.InvalidRequest,
            service.Create(user, true, request with { OperationId = "not-a-guid" }).Failure!.Code,
            "Malformed operation identity must be denied.");
        AssertEqual(0, store.BuildCalls, "Rejected package request reached Infrastructure.");
        AssertTrue(service.Create(user, true, request).Succeeded, "Trusted explicit request did not reach storage.");
        AssertEqual(1, store.BuildCalls, "Trusted explicit request did not reach Infrastructure exactly once.");
        var core = CallerPrincipal.CreateCoreInternal("wp20-core");
        AssertTrue(service.Create(core, false, request with { OperationId = "018f3e78-1234-7abc-8def-0123456789b5" }).Succeeded,
            "Core-owned scheduled backup was not permitted.");
        AssertEqual(ProjectPackageFailureCode.MutationDenied,
            service.Create(core, false, request with
            {
                Kind = ProjectPackageKind.Archive,
                OperationId = "018f3e78-1234-7abc-8def-0123456789b6"
            }).Failure!.Code,
            "Core-internal identity was incorrectly allowed to create an Archive.");
        AssertEqual(2, store.BuildCalls, "Denied automatic Archive reached Infrastructure.");
        return 9;
    }

    private static int ReplayIsIdempotentAndIdentityConflictStopsStorage()
    {
        var store = new FakeStore();
        var service = new ProjectPackageService(store, ProjectId);
        var user = new TrustedNativePrincipalSource("wp20-replay").ResolveUserInteractive();
        var first = new ProjectPackageRequest(ProjectPackageKind.Archive, ProjectId, OperationId, IncludeHistory: false);
        var replay = service.Create(user, true, first);
        var same = service.Create(user, true, first);
        var conflicting = service.Create(user, true, first with { IncludeHistory = true });

        AssertTrue(replay.Succeeded && same.Succeeded, "Identical replay was not accepted idempotently.");
        AssertEqual(replay.Value!.PackageId, same.Value!.PackageId, "Replay produced another package identity.");
        AssertEqual(ProjectPackageFailureCode.OperationIdentityConflict, conflicting.Failure!.Code,
            "Different request data reused an operation identity.");
        AssertEqual(1, store.BuildCalls, "Replay or conflict reached Infrastructure.");
        return 4;
    }

    private static int RuntimeAndCrossProjectIpcCannotReachPackageStore()
    {
        var store = new FakeStore();
        var holder = new ProjectPackageServiceHolder();
        holder.PublishOnce(new ProjectPackageService(store, ProjectId));
        var handler = new Wp20IpcCommandHandler(holder, "workspace-20");
        var runtime = Handle(handler, IpcClientKind.AgentRuntime, null, Guid.Parse(ProjectId), IpcSemanticTypes.CreateProjectBackup, "{\"operationId\":\"" + OperationId + "\"}");
        AssertEqual(IpcErrorCodes.PackageMutationDenied, Error(runtime).Code,
            "Runtime/renderer-originated request was not rejected before storage.");
        var user = new TrustedNativePrincipalSource("wp20-ipc").ResolveUserInteractive();
        var wrongProject = Handle(handler, IpcClientKind.Ui, user,
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789af"), IpcSemanticTypes.CreateProjectBackup,
            "{\"operationId\":\"" + OperationId + "\"}");
        AssertEqual(IpcErrorCodes.BindingMismatch, Error(wrongProject).Code,
            "Wrong project binding was not rejected before storage.");
        AssertEqual(0, store.BuildCalls, "Rejected IPC request reached package storage.");
        return 3;
    }

    private static byte[] Handle(
        Wp20IpcCommandHandler handler,
        IpcClientKind kind,
        LLMW.Writing.Application.Security.CallerPrincipal? principal,
        Guid projectId,
        string semanticType,
        string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
                kind,
                "connection-20",
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
        return result?.ResponseUtf8 ?? throw new InvalidOperationException("WP20 handler did not return a response.");
    }

    private static IpcError Error(byte[] response) =>
        IpcJson.Deserialize(response, IpcJsonContext.Default.ErrorEnvelope).Payload;

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

    private sealed class FakeStore : IProjectPackageStore
    {
        public int BuildCalls { get; private set; }

        public ProjectPackageStoreResult Build(ProjectPackageRequest request, CancellationToken cancellationToken = default)
        {
            BuildCalls++;
            return new ProjectPackageStoreResult(
                new ProjectPackageResult(request.Kind, request.OperationId, "safe.zip", DateTimeOffset.UnixEpoch, 0),
                null);
        }

        public ProjectPackageStoreVerification VerifyFinalPackage(string packageId, CancellationToken cancellationToken = default) =>
            new(new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.Verified, null), null);
    }
}
