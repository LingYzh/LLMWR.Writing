using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.AgentRuntime;

/// <summary>
/// Agent Runtime composition seam for WP14 Provider invocation.
/// Credentials stay in this process; Core IPC uses the authenticated WP11 session.
/// SqliteRuntimeStore is not referenced.
/// </summary>
internal static class ProviderInvocationRuntimeSeam
{
    internal static ProviderInvocationCoordinator Create(
        IpcClientSession session,
        RunSessionProof proof,
        IProviderDefinitionStore definitions,
        IProviderCredentialResolver credentials,
        IModelCertificationStore protocolProfiles,
        IPriceSnapshotStore prices,
        IProviderAdapterResolver adapters,
        IModelCatalogStore? catalog = null,
        MemoryTaskCertificationStore? taskCertifications = null) =>
        ProviderRuntimeComposition.Create(
            session,
            proof,
            definitions,
            credentials,
            protocolProfiles,
            prices,
            adapters,
            catalog,
            taskCertifications);
}
