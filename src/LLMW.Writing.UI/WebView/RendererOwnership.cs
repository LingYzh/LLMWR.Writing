namespace LLMW.Writing.UI.WebView;

internal sealed class RendererOwnership
{
    private int _generation;
    private object? _currentRenderer;
    private object? _registeredCore;
    private int _registeredGeneration;

    public int CurrentGeneration => _generation;

    public object? CurrentRenderer => _currentRenderer;

    public int AdoptRenderer(object renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _generation++;
        _currentRenderer = renderer;
        return _generation;
    }

    public bool IsCurrent(int expectedGeneration, object? renderer)
        => expectedGeneration > 0
           && expectedGeneration == _generation
           && renderer is not null
           && ReferenceEquals(renderer, _currentRenderer);

    public bool ShouldRegisterHandlers(int expectedGeneration, object? renderer, object? core)
    {
        if (core is null || !IsCurrent(expectedGeneration, renderer))
        {
            return false;
        }

        return !ReferenceEquals(_registeredCore, core) || _registeredGeneration != expectedGeneration;
    }

    public void MarkHandlersRegistered(int expectedGeneration, object renderer, object core)
    {
        if (!IsCurrent(expectedGeneration, renderer))
        {
            return;
        }

        _registeredCore = core;
        _registeredGeneration = expectedGeneration;
    }

    public bool HasHandlersFor(int generation, object? core)
        => generation > 0
           && generation == _registeredGeneration
           && core is not null
           && ReferenceEquals(_registeredCore, core);
}
