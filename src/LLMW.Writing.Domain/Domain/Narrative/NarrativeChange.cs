namespace LLMW.Writing.Domain.Narrative;

public enum NarrativeChangeKind
{
    Add,
    Modify,
    Remove,
    Reintroduce
}

public enum SemanticDependencyFinding
{
    Found,
    NoEvidenceFound,
    Uncertain
}

public enum NarrativeChangeDraftValidationFailure
{
    InvalidObjectIdentity,
    InvalidChangeOperation,
    BeforeStateRequired,
    BeforeStateNotAllowed,
    AfterPayloadRequired,
    AfterPayloadNotAllowed
}

public sealed record NarrativeChangeDraft(
    string ObjectId,
    string ObjectType,
    NarrativeChangeKind ChangeKind,
    string? BeforeRevisionRef,
    string? BeforeDigest,
    string? AfterPayloadDigest,
    int Ordinal)
{
    public NarrativeChangeDraftValidationFailure? Validate()
    {
        if (!IsCanonicalUuidV7(ObjectId))
        {
            return NarrativeChangeDraftValidationFailure.InvalidObjectIdentity;
        }

        var hasBefore = !string.IsNullOrWhiteSpace(BeforeRevisionRef) && !string.IsNullOrWhiteSpace(BeforeDigest);
        var hasAnyBefore = !string.IsNullOrWhiteSpace(BeforeRevisionRef) || !string.IsNullOrWhiteSpace(BeforeDigest);
        var hasAfter = !string.IsNullOrWhiteSpace(AfterPayloadDigest);

        return ChangeKind switch
        {
            NarrativeChangeKind.Add when string.IsNullOrWhiteSpace(ObjectType) =>
                NarrativeChangeDraftValidationFailure.InvalidChangeOperation,
            NarrativeChangeKind.Add when hasAnyBefore =>
                NarrativeChangeDraftValidationFailure.BeforeStateNotAllowed,
            NarrativeChangeKind.Add when !hasAfter =>
                NarrativeChangeDraftValidationFailure.AfterPayloadRequired,
            NarrativeChangeKind.Modify or NarrativeChangeKind.Reintroduce when !hasBefore =>
                NarrativeChangeDraftValidationFailure.BeforeStateRequired,
            NarrativeChangeKind.Modify or NarrativeChangeKind.Reintroduce when !hasAfter =>
                NarrativeChangeDraftValidationFailure.AfterPayloadRequired,
            NarrativeChangeKind.Remove when !hasBefore =>
                NarrativeChangeDraftValidationFailure.BeforeStateRequired,
            NarrativeChangeKind.Remove when hasAfter =>
                NarrativeChangeDraftValidationFailure.AfterPayloadNotAllowed,
            _ => null
        };
    }

    public static bool IsCanonicalUuidV7(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value, "D", out var id))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!id.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            return false;
        }

        return (bytes[6] >> 4) == 7 && StringComparer.Ordinal.Equals(value, id.ToString("D"));
    }
}
