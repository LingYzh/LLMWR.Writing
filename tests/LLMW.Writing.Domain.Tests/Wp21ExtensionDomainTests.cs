using LLMW.Writing.Domain.Extensions;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp21ExtensionDomainTests()
    {
        Run(nameof(ExtensionActivationRequiresTrustAndHasDeterministicTransitions), ExtensionActivationRequiresTrustAndHasDeterministicTransitions);
        Run(nameof(ExtensionCatalogUsesFrozenScopePrecedenceAndDeterministicComposition), ExtensionCatalogUsesFrozenScopePrecedenceAndDeterministicComposition);
    }

    private static void ExtensionActivationRequiresTrustAndHasDeterministicTransitions()
    {
        var denied = ExtensionActivationStateMachine.Transition(
            ExtensionActivationState.Inactive, ExtensionActivationEvent.Activate, projectTrusted: false);
        AssertTrue(!denied.Allowed, "Activation without Project Trust must be denied.");
        AssertEqual(ExtensionActivationRejection.ProjectTrustRequired, denied.Rejection!.Value,
            "Trust denial lost its typed reason.");

        var active = ExtensionActivationStateMachine.Transition(
            ExtensionActivationState.Inactive, ExtensionActivationEvent.Activate, projectTrusted: true);
        AssertEqual(ExtensionActivationState.Active, active.NextState!.Value,
            "Trusted activation did not reach ACTIVE.");
        var invalidated = ExtensionActivationStateMachine.Transition(
            active.NextState.Value, ExtensionActivationEvent.ContentChanged, projectTrusted: true);
        AssertEqual(ExtensionActivationState.Invalidated, invalidated.NextState!.Value,
            "Changed executable/config content must invalidate activation.");
        var reactivated = ExtensionActivationStateMachine.Transition(
            invalidated.NextState.Value, ExtensionActivationEvent.Activate, projectTrusted: true);
        AssertEqual(ExtensionActivationState.Active, reactivated.NextState!.Value,
            "Explicit reactivation after a changed digest was rejected.");
        var revoked = ExtensionActivationStateMachine.Transition(
            reactivated.NextState.Value, ExtensionActivationEvent.TrustRevoked, projectTrusted: false);
        AssertEqual(ExtensionActivationState.Inactive, revoked.NextState!.Value,
            "Trust revocation must deactivate extensions.");
        var invalid = ExtensionActivationStateMachine.Transition(
            revoked.NextState.Value, ExtensionActivationEvent.Deactivate, projectTrusted: false);
        AssertTrue(!invalid.Allowed && invalid.Rejection == ExtensionActivationRejection.AlreadyInactive,
            "Invalid INACTIVE → DEACTIVATE transition was not rejected.");
    }

    private static void ExtensionCatalogUsesFrozenScopePrecedenceAndDeterministicComposition()
    {
        var appBase = Descriptor(ExtensionKind.Skill, "base", ExtensionScope.Application, "a");
        var userBase = Descriptor(ExtensionKind.Skill, "base", ExtensionScope.User, "b");
        var projectBase = Descriptor(ExtensionKind.Skill, "base", ExtensionScope.Project, "c");
        var appResearch = Descriptor(ExtensionKind.Skill, "research", ExtensionScope.Application, "d");
        var userWriter = Descriptor(ExtensionKind.Skill, "writer", ExtensionScope.User, "e");
        var projectReviewer = Descriptor(ExtensionKind.Skill, "reviewer", ExtensionScope.Project, "f");
        var resolved = ExtensionCatalogResolver.Resolve(
            [projectReviewer, userWriter, appResearch, userBase, projectBase, appBase]);

        AssertEqual(4, resolved.Extensions.Count, "Same persistent Skill name was not overridden by nearer scope.");
        AssertEqual(ExtensionScope.Project, resolved.Extensions.Single(item => item.Id == "skill:base").Scope,
            "Project Skill did not override User/Application Skill of the same name.");
        AssertEqual(
            "skill:research,skill:writer,skill:base,skill:reviewer",
            string.Join(',', resolved.Extensions.Select(item => item.Id)),
            "Different Skill composition must be Application → User → Project with stable in-scope ordering.");

        var duplicate = ExtensionCatalogResolver.Resolve([appBase, Descriptor(ExtensionKind.Skill, "base", ExtensionScope.Application, "f")]);
        AssertEqual(0, duplicate.Extensions.Count, "Same-scope duplicate must not produce an ambiguous activation target.");
        AssertEqual("EXTENSION_DUPLICATE_SCOPE", duplicate.Diagnostics.Single().Code,
            "Same-scope conflict diagnostic is missing.");
    }

    private static ExtensionDescriptor Descriptor(ExtensionKind kind, string name, ExtensionScope scope, string seed) =>
        new(
            new ExtensionManifest(
                kind,
                name,
                "1.0.0",
                "safe",
                "instruction " + name,
                [],
                [Capability.ProjectFileRead],
                []),
            scope,
            new string(seed[0], 64));
}
