using LLMW.Writing.Domain.Narrative;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private const string NarrativeObjectId = "018f3e78-1234-7abc-8def-0123456789ab";
    private const string StateRevisionId = "018f3e78-1234-7abc-8def-0123456789ac";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static void RunWp06NarrativeChangeDomainTests()
    {
        Run(nameof(NarrativeChangeDraftsValidateFourFrozenOperations), NarrativeChangeDraftsValidateFourFrozenOperations);
        Run(nameof(NarrativeChangeDraftsRejectInvalidSidesAndNonUuidV7Identity), NarrativeChangeDraftsRejectInvalidSidesAndNonUuidV7Identity);
    }

    private static void NarrativeChangeDraftsValidateFourFrozenOperations()
    {
        NarrativeChangeDraft[] valid =
        [
            new(NarrativeObjectId, "character", NarrativeChangeKind.Add, null, null, Digest, 0),
            new(NarrativeObjectId, "character", NarrativeChangeKind.Modify, StateRevisionId, Digest, Digest, 1),
            new(NarrativeObjectId, "character", NarrativeChangeKind.Remove, StateRevisionId, Digest, null, 2),
            new(NarrativeObjectId, "character", NarrativeChangeKind.Reintroduce, StateRevisionId, Digest, Digest, 3)
        ];

        foreach (var change in valid)
        {
            AssertEqual<NarrativeChangeDraftValidationFailure?>(null, change.Validate(),
                $"{change.ChangeKind} was rejected despite a valid frozen representation.");
        }
    }

    private static void NarrativeChangeDraftsRejectInvalidSidesAndNonUuidV7Identity()
    {
        var addWithBefore = new NarrativeChangeDraft(
            NarrativeObjectId, "character", NarrativeChangeKind.Add, StateRevisionId, Digest, Digest, 0);
        AssertEqual(NarrativeChangeDraftValidationFailure.BeforeStateNotAllowed, addWithBefore.Validate(),
            "ADD accepted a Current Narrative before side.");

        var removeWithAfter = new NarrativeChangeDraft(
            NarrativeObjectId, "character", NarrativeChangeKind.Remove, StateRevisionId, Digest, Digest, 0);
        AssertEqual(NarrativeChangeDraftValidationFailure.AfterPayloadNotAllowed, removeWithAfter.Validate(),
            "REMOVE accepted a replacement payload.");

        var nonV7 = new NarrativeChangeDraft(
            "018f3e78-1234-4abc-8def-0123456789ab", "character", NarrativeChangeKind.Add, null, null, Digest, 0);
        AssertEqual(NarrativeChangeDraftValidationFailure.InvalidObjectIdentity, nonV7.Validate(),
            "A durable Narrative Object identity was accepted without UUIDv7 layout.");
    }
}
