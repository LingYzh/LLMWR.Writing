using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp09SecurityDomainTests()
    {
        Run(nameof(CapabilityMatrixMatchesFrozenGoldenTable), CapabilityMatrixMatchesFrozenGoldenTable);
        Run(nameof(RoleDenySurvivesBypassPermissions), RoleDenySurvivesBypassPermissions);
        Run(nameof(HardDenySurvivesEveryPermissionMode), HardDenySurvivesEveryPermissionMode);
        Run(nameof(ScopedAskNeverSilentlyElevates), ScopedAskNeverSilentlyElevates);
        Run(nameof(NarrativeAuthorityIsOrthogonalToPermission), NarrativeAuthorityIsOrthogonalToPermission);
        Run(nameof(CanonicalRoleAndCapabilityMappingsAreComplete), CanonicalRoleAndCapabilityMappingsAreComplete);
        Run(nameof(MissingPolicyLayersFailClosed), MissingPolicyLayersFailClosed);
        Run(nameof(CoreInternalDoesNotBypassHardDeny), CoreInternalDoesNotBypassHardDeny);
    }

    private static void CapabilityMatrixMatchesFrozenGoldenTable()
    {
        var allowed = RoleCapabilityLevel.Allowed;
        var scoped = RoleCapabilityLevel.Scoped;
        var denied = RoleCapabilityLevel.Denied;
        var expected = new Dictionary<AgentRole, RoleCapabilityLevel[]>
        {
            [AgentRole.PmMainOrchestrator] =
                [allowed, scoped, scoped, scoped, scoped, denied, scoped, allowed, denied, allowed, scoped, scoped, scoped, scoped, scoped, allowed],
            [AgentRole.DataOps] =
                [allowed, denied, allowed, allowed, denied, denied, denied, allowed, allowed, allowed, scoped, scoped, scoped, denied, scoped, scoped],
            [AgentRole.StoryPlanner] =
                [allowed, denied, denied, allowed, denied, denied, denied, allowed, denied, allowed, scoped, scoped, scoped, denied, scoped, scoped],
            [AgentRole.Writer] =
                [allowed, allowed, denied, denied, scoped, denied, denied, allowed, denied, allowed, scoped, scoped, scoped, denied, scoped, scoped],
            [AgentRole.Reviewer] =
                [allowed, denied, denied, denied, denied, allowed, denied, allowed, denied, allowed, scoped, scoped, scoped, denied, scoped, scoped],
            [AgentRole.Researcher] =
                [allowed, denied, scoped, denied, denied, denied, denied, allowed, denied, allowed, scoped, scoped, scoped, denied, scoped, denied]
        };

        AssertEqual(Enum.GetValues<AgentRole>().Length, expected.Count, "Golden role matrix is incomplete.");
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var row = expected[role];
            AssertEqual(Enum.GetValues<Capability>().Length, row.Length, $"Golden capability row is incomplete for {role}.");
            foreach (var capability in Enum.GetValues<Capability>())
            {
                AssertEqual(row[(int)capability], RoleCapabilityMatrix.Get(role, capability),
                    $"Frozen capability matrix drifted for {role}/{capability}.");
            }
        }
    }

    private static void RoleDenySurvivesBypassPermissions()
    {
        var decision = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.AuthorityAccept,
            PrincipalKind.AgentRun,
            AgentRole.Reviewer,
            RuntimePermissionMode.BypassPermissions,
            NarrativeAuthorityAvailable: true));

        AssertEqual(CapabilityDecisionKind.Denied, decision.Decision, "BYPASS elevated a frozen role DENY.");
        AssertEqual(CapabilityDecisionReason.RoleDenied, decision.Reasons.Single(), "Role denial reason was not preserved.");
    }

    private static void HardDenySurvivesEveryPermissionMode()
    {
        foreach (var mode in Enum.GetValues<RuntimePermissionMode>())
        {
            var decision = CapabilityEvaluator.Evaluate(TrustedEvaluation(
                Capability.ShellExecute,
                PrincipalKind.AgentRun,
                AgentRole.PmMainOrchestrator,
                mode,
                HardDeny: HardDeny.OutsideProjectDestructive));
            AssertEqual(CapabilityDecisionKind.Denied, decision.Decision, $"{mode} bypassed HardDeny.");
            AssertTrue(decision.HardDenied, $"{mode} denial omitted hard-deny evidence.");
        }
    }

    private static void ScopedAskNeverSilentlyElevates()
    {
        var decision = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.AuthoritySubmit,
            PrincipalKind.AgentRun,
            AgentRole.Writer,
            RuntimePermissionMode.Ask));

        AssertEqual(CapabilityDecisionKind.RequiresApproval, decision.Decision,
            "ASK silently elevated a scoped Writer submission.");
    }

    private static void NarrativeAuthorityIsOrthogonalToPermission()
    {
        var pm = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.AuthorityAccept,
            PrincipalKind.AgentRun,
            AgentRole.PmMainOrchestrator,
            RuntimePermissionMode.BypassPermissions));
        var reviewer = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.AuthorityAccept,
            PrincipalKind.AgentRun,
            AgentRole.Reviewer,
            RuntimePermissionMode.BypassPermissions,
            NarrativeAuthorityAvailable: true));
        var writerReview = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.AuthorityReview,
            PrincipalKind.AgentRun,
            AgentRole.Writer,
            RuntimePermissionMode.BypassPermissions));

        AssertEqual(CapabilityDecisionReason.NarrativeAuthorityRequired, pm.Reasons.Single(),
            "PM BYPASS incorrectly established delegated Narrative Authority.");
        AssertEqual(CapabilityDecisionReason.RoleDenied, reviewer.Reasons.Single(),
            "Reviewer BYPASS incorrectly gained Authority.Accept.");
        AssertEqual(CapabilityDecisionReason.RoleDenied, writerReview.Reasons.Single(),
            "Writer BYPASS incorrectly gained Authority.Review.");
    }

    private static void CanonicalRoleAndCapabilityMappingsAreComplete()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var value = AgentRoleCodec.ToDurableValue(role);
            AssertTrue(AgentRoleCodec.TryParse(value, out var parsed), $"Durable role {value} did not parse.");
            AssertEqual(role, parsed, $"Durable role {value} changed identity.");
        }

        var names = Enum.GetValues<Capability>().Select(CapabilityCodec.ToCanonicalName).ToArray();
        AssertEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count(), "Canonical capability names are not unique.");
        AssertTrue(names.Contains("Shell.Execute", StringComparer.Ordinal), "Shell.Execute is missing.");
        AssertTrue(names.Contains("Script.Execute", StringComparer.Ordinal), "Script.Execute was merged into Shell.Execute.");
    }

    private static void MissingPolicyLayersFailClosed()
    {
        var baseline = TrustedEvaluation(
            Capability.ShellExecute,
            PrincipalKind.AgentRun,
            AgentRole.PmMainOrchestrator,
            RuntimePermissionMode.BypassPermissions);
        AssertEqual(CapabilityDecisionReason.ToolGrantMissing,
            CapabilityEvaluator.Evaluate(baseline with { ToolGranted = false }).Reasons.Single(),
            "Missing tool grant failed open.");
        AssertEqual(CapabilityDecisionReason.ExtensionGrantMissing,
            CapabilityEvaluator.Evaluate(baseline with { ExtensionGranted = false }).Reasons.Single(),
            "Missing extension activation failed open.");
        AssertEqual(CapabilityDecisionReason.TrustRequired,
            CapabilityEvaluator.Evaluate(baseline with { ProjectTrusted = false }).Reasons.Single(),
            "Missing Project Trust failed open.");
        AssertEqual(CapabilityDecisionReason.PathOutOfScope,
            CapabilityEvaluator.Evaluate(baseline with { Scope = SecurityScopeClassification.OutOfScope }).Reasons.Single(),
            "Unknown/out-of-scope path failed open.");
    }

    private static void CoreInternalDoesNotBypassHardDeny()
    {
        var decision = CapabilityEvaluator.Evaluate(TrustedEvaluation(
            Capability.RegistryMutate,
            PrincipalKind.CoreInternal,
            null,
            RuntimePermissionMode.BypassPermissions,
            HardDeny: HardDeny.RegistryOrSystemWrite));
        AssertEqual(CapabilityDecisionKind.Denied, decision.Decision,
            "CORE_INTERNAL became a universal HardDeny bypass.");
        AssertTrue(decision.HardDenied, "CORE_INTERNAL denial omitted HardDeny evidence.");
    }

    private static CapabilityEvaluationRequest TrustedEvaluation(
        Capability capability,
        PrincipalKind principalKind,
        AgentRole? role,
        RuntimePermissionMode permissionMode,
        HardDeny HardDeny = HardDeny.None,
        bool NarrativeAuthorityAvailable = false,
        bool ExplicitUserTask = false) =>
        new(
            capability,
            principalKind,
            role,
            permissionMode,
            ProductAllowed: true,
            ToolGranted: true,
            ExtensionGranted: true,
            ProjectTrusted: true,
            Scope: SecurityScopeClassification.InScope,
            HardDeny,
            NarrativeAuthorityAvailable,
            ExplicitUserTask);
}
