using LLMW.Writing.Application.Authority;

namespace LLMW.Writing.Application.ChapterAuthority;

public interface IChapterReviewer
{
    ChapterReviewDecision Review(
        CandidateReviewInput candidate,
        Stream candidateContent,
        CancellationToken cancellationToken = default);
}

public interface IChapterAuthorityStore
{
    SubmissionContext? LoadSubmissionContext(string chapterId);

    SubmitChapterDraftResult PersistCandidate(
        AuthorityTransactionHandle transaction,
        PersistCandidateRequest request);

    CandidateReviewContext? LoadReviewContext(string candidateId);

    ReviewChapterCandidateResult PersistReview(PersistReviewRequest request);

    CandidateAcceptanceContext? LoadAcceptanceContext(string candidateId);

    CandidateAcceptanceContext PrepareAcceptance(PrepareAcceptanceRequest request);

    AcceptChapterCandidateResult CommitAcceptance(
        CandidateAcceptanceContext context,
        CancellationToken cancellationToken = default);

    AcceptChapterCandidateResult RecoverAcceptance(
        CandidateAcceptanceContext context,
        CancellationToken cancellationToken = default);
}
