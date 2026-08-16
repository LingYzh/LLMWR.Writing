namespace LLMW.Writing.Domain.Runtime;

/// <summary>
/// Store-owned monotonic created_at_ms for runtime writes whose relative order is
/// security-relevant. Callers supply wall-clock; persisted max is authoritative.
/// </summary>
public static class RuntimeLogicalTimestamp
{
    public static long Allocate(long wallClockMs, long? persistedMaxCreatedAtMs)
    {
        if (persistedMaxCreatedAtMs is null)
        {
            return wallClockMs;
        }

        var next = persistedMaxCreatedAtMs.Value + 1;
        if (next < persistedMaxCreatedAtMs.Value)
        {
            return persistedMaxCreatedAtMs.Value;
        }

        return wallClockMs >= next ? wallClockMs : next;
    }
}
