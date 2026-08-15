using LLMW.Writing.Application.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp11InfrastructureTests()
    {
        Run(nameof(PersistedRunSessionExpiryIsCoreClampedValue), PersistedRunSessionExpiryIsCoreClampedValue);
    }

    private static void PersistedRunSessionExpiryIsCoreClampedValue()
    {
        using var database = MigratedDatabase.Create();
        SeedRun(database.Path, "run-wp11-ttl", "writer");
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(50_000));
        var service = new RunSessionService(new SqliteRunSessionStore(database.Path), clock);
        var issued = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp11-ttl",
            Channel("worker-ttl", "channel-ttl", "workspace-a"),
            clock.UtcNow.AddYears(40))));
        var expected = clock.UtcNow.AddHours(8).ToUnixTimeMilliseconds();
        Wp09AssertEqual(expected, issued.ExpiresAt.ToUnixTimeMilliseconds(), "Issued expiry is not the Core maximum.");

        using var connection = OpenConfigured(database.Path);
        Wp09AssertEqual(
            expected,
            Scalar<long>(connection, $"SELECT expires_at_ms FROM run_session_handles WHERE handle_id='{issued.HandleId}';"),
            "Persisted expires_at_ms is not the clamped Core expiry.");
    }
}
