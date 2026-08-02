namespace AirBlade.Services;

public static class AirplaySessionRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<AirplaySessionService> Sessions = [];
    internal static void Register(AirplaySessionService service) { lock (Gate) Sessions.Add(service); }
    internal static void Unregister(AirplaySessionService service) { lock (Gate) Sessions.Remove(service); }
    public static async Task DisposeAllAsync()
    {
        AirplaySessionService[] snapshot;
        lock (Gate) snapshot = Sessions.ToArray();
        await Task.WhenAll(snapshot.Select(service => service.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
