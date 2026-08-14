using LLMW.Writing.Application.Reconcile;

namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class SqliteAuthoritySurfaceHealthGate : IAuthoritySurfaceHealthGate
{
    private readonly ProjectReconcileEngine engine;

    public SqliteAuthoritySurfaceHealthGate(ProjectReconcileEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public AuthoritySurfaceHealth Check(
        AuthoritySurfaceHealthRequest request,
        CancellationToken cancellationToken = default) =>
        engine.InspectAuthorityHealthFresh(request, cancellationToken);
}
