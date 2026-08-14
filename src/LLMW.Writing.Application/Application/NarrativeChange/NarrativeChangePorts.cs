namespace LLMW.Writing.Application.NarrativeChange;

public interface ISemanticDependencyAssessor
{
    SemanticDependencyAssessment Assess(
        NarrativeChangeSetSnapshot changeSet,
        CancellationToken cancellationToken = default);
}

public interface INarrativeImpactAnalyzer
{
    NarrativeImpactAnalysisResult Analyze(
        NarrativeChangeSetSnapshot changeSet,
        StructuralDependencyAssessment structuralAssessment,
        SemanticDependencyAssessment semanticAssessment,
        CancellationToken cancellationToken = default);
}

public interface INarrativeChangeStore
{
    NarrativeStoreResult<NarrativeChangeSetSnapshot> CreateWorkingChangeSet(PersistWorkingChangeSetRequest request);

    NarrativeChangeSetSnapshot? LoadChangeSet(string changeSetId);

    StructuralDependencyAssessment AssessStructuralDependencies(NarrativeChangeSetSnapshot changeSet);

    NarrativeStoreResult<NarrativeImpactAnalysisRecord> PersistImpactAnalysis(PersistImpactAnalysisRequest request);

    NarrativeStoreResult<NarrativeApplyStoreResult> Apply(
        NarrativeApplyStoreRequest request,
        CancellationToken cancellationToken = default);
}
