using System.Security.Cryptography;
using LLMW.Writing.Application.Reconcile;

namespace LLMW.Writing.Infrastructure.FileSystem;

public sealed class SelfWriteTracker : ISelfWriteTracker
{
    private readonly object sync = new();
    private readonly TimeSpan retention;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, OperationState> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationState> recent = new(StringComparer.Ordinal);

    public SelfWriteTracker(TimeSpan? retention = null, Func<DateTimeOffset>? clock = null)
    {
        this.retention = retention ?? TimeSpan.FromSeconds(30);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ISelfWriteOperation BeginOperation(IReadOnlyList<SelfWriteExpectation> expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        var normalized = expectations
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                item => NormalizePath(item.RelativePath),
                item => ImmutableBlobStore.NormalizeDigest(item.ExpectedPhysicalDigest),
                StringComparer.OrdinalIgnoreCase);
        var token = RandomNumberGenerator.GetHexString(24).ToLowerInvariant();
        lock (sync)
        {
            CleanupExpired();
            active.Add(token, new OperationState(token, normalized, DateTimeOffset.MaxValue));
        }

        return new Operation(this, token);
    }

    public string? TryGetActiveToken(string relativePath)
    {
        var path = NormalizePath(relativePath);
        lock (sync)
        {
            CleanupExpired();
            return active.Values
                .OrderBy(item => item.Token, StringComparer.Ordinal)
                .FirstOrDefault(item => item.Expectations.ContainsKey(path))?.Token;
        }
    }

    public bool ShouldSuppress(
        string? operationToken,
        string relativePath,
        string? observedPhysicalDigest)
    {
        if (string.IsNullOrWhiteSpace(observedPhysicalDigest))
        {
            return false;
        }

        var path = NormalizePath(relativePath);
        string digest;
        try
        {
            digest = ImmutableBlobStore.NormalizeDigest(observedPhysicalDigest);
        }
        catch (ArgumentException)
        {
            return false;
        }

        lock (sync)
        {
            CleanupExpired();
            if (!string.IsNullOrWhiteSpace(operationToken) &&
                (active.TryGetValue(operationToken, out var activeOperation) ||
                 recent.TryGetValue(operationToken, out activeOperation)) &&
                Matches(activeOperation, path, digest))
            {
                return true;
            }

            return active.Values.Concat(recent.Values).Any(item => Matches(item, path, digest));
        }
    }

    private static bool Matches(OperationState operation, string path, string digest) =>
        operation.Expectations.TryGetValue(path, out var expected) &&
        StringComparer.Ordinal.Equals(expected, digest);

    private void Complete(string token)
    {
        lock (sync)
        {
            if (!active.Remove(token, out var operation))
            {
                return;
            }

            recent[token] = operation with { ExpiresAt = clock() + retention };
            CleanupExpired();
        }
    }

    private void CleanupExpired()
    {
        var now = clock();
        foreach (var token in recent.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
        {
            recent.Remove(token);
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed record OperationState(
        string Token,
        IReadOnlyDictionary<string, string> Expectations,
        DateTimeOffset ExpiresAt);

    private sealed class Operation : ISelfWriteOperation
    {
        private readonly SelfWriteTracker owner;
        private int disposed;

        public Operation(SelfWriteTracker owner, string token)
        {
            this.owner = owner;
            Token = token;
        }

        public string Token { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Complete(Token);
            }
        }
    }
}
