/// <summary>Prevents a failed or completed route world from being built again every frame.</summary>
public enum RouteWorldLoadState { NotStarted, Loading, Loaded, Failed }

public sealed class RouteWorldLoadAttempt<T> where T : class
{
    private T? _route;
    public RouteWorldLoadState State { get; private set; } = RouteWorldLoadState.NotStarted;
    public string? Error { get; private set; }

    public bool TryBegin(T route)
    {
        if (!ReferenceEquals(_route, route))
        {
            _route = route;
            State = RouteWorldLoadState.NotStarted;
            Error = null;
        }
        if (State != RouteWorldLoadState.NotStarted) return false;
        State = RouteWorldLoadState.Loading;
        return true;
    }

    public void MarkLoaded(T route)
    {
        EnsureCurrent(route);
        State = RouteWorldLoadState.Loaded;
        Error = null;
    }

    public void MarkFailed(T route, string error)
    {
        EnsureCurrent(route);
        State = RouteWorldLoadState.Failed;
        Error = error;
    }

    /// <summary>A new ride start is the intentional recovery action after a failed load.</summary>
    public void Reset()
    {
        _route = null;
        State = RouteWorldLoadState.NotStarted;
        Error = null;
    }

    private void EnsureCurrent(T route)
    {
        if (!ReferenceEquals(_route, route) || State != RouteWorldLoadState.Loading)
            throw new System.InvalidOperationException("Route world load completion does not match the active attempt.");
    }
}
