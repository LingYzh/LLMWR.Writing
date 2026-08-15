namespace LLMW.Writing.Application.Security;

/// <summary>
/// Core-owned RunSession lifetime. Caller timestamps are requested upper bounds only.
/// </summary>
public sealed class RunSessionTtlPolicy
{
    public static RunSessionTtlPolicy Production { get; } = new(TimeSpan.FromHours(1), TimeSpan.FromHours(8));

    public RunSessionTtlPolicy(TimeSpan defaultTtl, TimeSpan maximumTtl)
    {
        DefaultTtl = defaultTtl;
        MaximumTtl = maximumTtl;
    }

    public TimeSpan DefaultTtl { get; }

    public TimeSpan MaximumTtl { get; }

    public bool IsValid =>
        DefaultTtl > TimeSpan.Zero && MaximumTtl > TimeSpan.Zero && DefaultTtl <= MaximumTtl;

    public DateTimeOffset Clamp(DateTimeOffset now, DateTimeOffset requested)
    {
        var deadline = now + MaximumTtl;
        return requested <= deadline ? requested : deadline;
    }
}
