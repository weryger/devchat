using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DevChat.Core.Bridge;

/// <summary>
/// WebSocket-сервер моста на 127.0.0.1. Ждёт подключение расширения,
/// маршрутизирует запросы/ответы по id. Протокол — в docs/PLAN.md.
/// </summary>
public sealed class BridgeServer
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly int _port;
    private readonly string _token;
    private int _idSeq;
    private WebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<string>? Delta;          // частичный текст ответа
    public event Action<string>? Phase;          // фаза хода: sent (запрос принят моделью)
    public event Action<string, string>? Error;  // code, message
    public event Action<string>? Log;            // диагностические сообщения моста

    public bool IsClientConnected => _ws is { State: WebSocketState.Open };

    public BridgeServer(int port, string token)
    {
        _port = port;
        _token = token;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        Log?.Invoke($"мост слушает ws://127.0.0.1:{_port}/");
        _ = PingLoopAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"ошибка прослушивания: {ex.Message}");
                break;
            }

            try
            {
                if (ctx.Request.QueryString["token"] != _token)
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    continue;
                }
                if (!ctx.Request.IsWebSocketRequest)
                {
                    // HTTP-проверка доступности без апгрейда
                    var hello = Encoding.UTF8.GetBytes($"DevChat bridge (ws ok, clients: {IsClientConnected})");
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.OutputStream.WriteAsync(hello, ct);
                    ctx.Response.Close();
                    continue;
                }

                // подпротокол берём только из предложенных клиентом, иначе null
                var offered = ctx.Request.Headers["Sec-WebSocket-Protocol"];
                var proto = offered?.Split(',').Select(s => s.Trim()).FirstOrDefault(p => p.Length > 0);
                var wsc = await ctx.AcceptWebSocketAsync(proto);
                _ = HandleClientAsync(wsc.WebSocket, ct); // фон: приёмная петля не должна блокироваться на клиенте
            }
            catch (Exception ex)
            {
                Log?.Invoke($"ошибка обработки подключения: {ex.GetType().Name}: {ex.Message}");
                try { ctx.Response.Abort(); } catch { /* уже закрыт */ }
            }
        }
    }

    private async Task HandleClientAsync(WebSocket ws, CancellationToken ct)
    {
        // новый клиент вытесняет старого
        if (_ws is { State: WebSocketState.Open } old)
            _ = old.CloseAsync(WebSocketCloseStatus.PolicyViolation, "replaced", CancellationToken.None);
        _ws = ws;
        Log?.Invoke("расширение подключено");
        ClientConnected?.Invoke();
        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"соединение завершено нештатно: {ex.GetType().Name}: {ex.Message}");
        }
        Log?.Invoke("расширение отключено");
        ClientDisconnected?.Invoke();
        if (ReferenceEquals(_ws, ws)) _ws = null;
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                ms.Write(buffer, 0, res.Count);
            }
            while (!res.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            HandleMessage(json);
        }
    }

    private void HandleMessage(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { Log?.Invoke($"не-JSON от расширения: {json[..Math.Min(200, json.Length)]}"); return; }

        // события без id (журнал и фазы расширения)
        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
        {
            if (root.TryGetProperty("type", out var et) && et.GetString() == "event"
                && root.TryGetProperty("name", out var en) && root.TryGetProperty("text", out var ex))
            {
                var name = en.GetString();
                var text = ex.GetString();
                if (name == "phase") Phase?.Invoke(text ?? "");
                Log?.Invoke($"[ext:{name}] {text}");
            }
            return;
        }

        var id = idEl.GetString()!;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "delta":
                var text = root.TryGetProperty("text", out var d) ? d.GetString() : null;
                if (text != null) Delta?.Invoke(text);
                break;
            case "error":
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : "unknown";
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "";
                Error?.Invoke(code!, msg ?? "");
                if (_pending.TryRemove(id, out var errTcs))
                    errTcs.TrySetResult(root);
                break;
            case "result":
            case "pong":
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(root);
                break;
        }
    }

    /// <summary>Запрос к расширению с ожиданием ответа (result/error/pong) по id. Отменяемый.</summary>
    public async Task<JsonElement> RequestAsync(string op, object? args = null, int timeoutMs = 240_000, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("расширение не подключено");

        var id = $"c{Interlocked.Increment(ref _idSeq)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = new Dictionary<string, object?> { ["id"] = id, ["op"] = op };
        if (args is Dictionary<string, object?> dict)
            foreach (var kv in dict) payload[kv.Key] = kv.Value;

        await SendAsync(JsonSerializer.Serialize(payload));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        cts.Token.Register(() =>
        {
            if (!_pending.TryRemove(id, out var stale)) return;
            if (ct.IsCancellationRequested) stale.TrySetCanceled(ct);
            else stale.TrySetException(new TimeoutException(
                $"расширение не ответило на '{op}' за {timeoutMs / 1000}с (соединение при этом {(IsClientConnected ? "открыто — похоже, завис service worker; перезапусти его в chrome://extensions" : "закрыто — Chrome не запущен или вкладка с расширением умерла")}"));
        });
        return await tcs.Task;
    }

    private async Task SendAsync(string json)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) throw new InvalidOperationException("расширение не подключено");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        // держит MV3 service worker расширения живым
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            if (!IsClientConnected) continue;
            try { await RequestAsync("ping", timeoutMs: 10_000); }
            catch (TimeoutException) { Log?.Invoke("расширение не отвечает на ping"); }
            catch (Exception) { /* соединение рвётся — reconnect на стороне расширения */ }
        }
    }
}
