using System.Text.Json;
using DevChat.Core.Bridge;

namespace DevChat.App.Services;

/// <summary>Обёртка моста для UI: события состояния + запросы.</summary>
public sealed class BridgeService
{
    private BridgeServer? _server;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<string>? Delta;
    public event Action<string>? Phase;
    public event Action<string>? Log;

    public bool IsConnected => _server?.IsClientConnected ?? false;

    public Task RunAsync(int port, string token, CancellationToken ct)
    {
        var server = new BridgeServer(port, token);
        server.ClientConnected += () => Connected?.Invoke();
        server.ClientDisconnected += () => Disconnected?.Invoke();
        server.Delta += t => Delta?.Invoke(t);
        server.Phase += p => Phase?.Invoke(p);
        server.Log += m => Log?.Invoke(m);
        _server = server;
        return server.RunAsync(ct);
    }

    public Task<JsonElement> RequestAsync(string op, object? args = null, int timeoutMs = 240_000, CancellationToken ct = default) =>
        (_server ?? throw new InvalidOperationException("мост не запущен")).RequestAsync(op, args, timeoutMs, ct);
}
