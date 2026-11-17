using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevChat.Core.Bridge;

// CLI моста: REPL + e2e-тесты. Использование:
//   dotnet run --project src/Cli [-- port token]    — REPL
//   dotnet run --project src/Cli -- auto [промпт]   — e2e: probe + промпт
//   dotnet run --project src/Cli -- auto dump       — дамп DOM DeepSeek
Console.OutputEncoding = Encoding.UTF8;

var auto = args.Length > 0 && args[0] == "auto";
var port = !auto && args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8765;
var token = !auto && args.Length > 1 ? args[1] : "DevChat-dev-token";

using var cts = new CancellationTokenSource();
var bridge = new BridgeServer(port, token);
var deltaSb = new StringBuilder();
var gate = new object();

bridge.Log += m => Console.WriteLine($"[bridge] {m}");
bridge.Delta += text => { lock (gate) { deltaSb.Append(text); Console.Write(text); } };
bridge.Error += (code, msg) => Console.WriteLine($"\n[bridge:{code}] {msg}");

var serverTask = bridge.RunAsync(cts.Token);
_ = serverTask.ContinueWith(
    t => Console.WriteLine($"[bridge] фатальная ошибка сервера: {t.Exception!.GetBaseException().Message}"),
    TaskContinuationOptions.OnlyOnFaulted);

Console.WriteLine("=== DevChat bridge CLI ===");
Console.WriteLine($"WebSocket: ws://127.0.0.1:{port}/?token={token}");
Console.WriteLine("Команды: /status /probe /dump /cancel /quit; всё остальное — промпт в DeepSeek.");
Console.CancelKeyPress += (_, _) => cts.Cancel();

if (auto)
{
    if (args.Contains("dump"))
    {
        var provider = args.Contains("chatgpt") ? "chatgpt" : "deepseek";
        Console.WriteLine($"[auto] ожидание расширения (до 3 мин)… [провайдер {provider}]");
        if (!await WaitForExtensionAsync(bridge, cts.Token)) return;
        await RunAutoStepAsync(bridge, "dump", new Dictionary<string, object?> { ["provider"] = provider });
        Console.WriteLine("[auto] завершено (режим dump)");
        return;
    }

    var prompt = args.Length > 1 ? args[1] : "Ответь ровно одним предложением: мост DevChat работает.";
    Console.WriteLine("[auto] ожидание расширения (до 3 мин)…");
    if (!await WaitForExtensionAsync(bridge, cts.Token)) return;
    await RunAutoStepAsync(bridge, "probe");
    await RunAutoStepAsync(bridge, "sendPrompt", new Dictionary<string, object?> { ["text"] = prompt }, printMarkdown: true);
    Console.WriteLine("[auto] завершено");
    return;
}

while (!cts.IsCancellationRequested)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (line.Length == 0) continue;

    var sw = Stopwatch.StartNew();
    try
    {
        switch (line)
        {
            case "/status":
                PrintResult(await bridge.RequestAsync("getStatus", timeoutMs: 15_000));
                break;
            case "/probe":
                PrintResult(await bridge.RequestAsync("probe", timeoutMs: 30_000));
                break;
            case "/dump":
                PrintResult(await bridge.RequestAsync("dump", timeoutMs: 30_000));
                break;
            case "/cancel":
                await bridge.RequestAsync("cancel", timeoutMs: 15_000);
                Console.WriteLine("[отмена отправлена]");
                break;
            default:
                Console.WriteLine("→ отправка…");
                lock (gate) deltaSb.Clear();
                var result = await bridge.RequestAsync("sendPrompt", new Dictionary<string, object?> { ["text"] = line });
                lock (gate) { if (deltaSb.Length > 0) Console.WriteLine(); }
                if (result.TryGetProperty("type", out var t) && t.GetString() == "error")
                    PrintResult(result);
                else if (result.TryGetProperty("markdown", out var md))
                    Console.WriteLine($"\n[готово за {sw.Elapsed.TotalSeconds:0.0}s, {md.GetString()?.Length ?? 0} символов]");
                break;
        }
    }
    catch (TimeoutException ex) { Console.WriteLine($"\n[таймаут] {ex.Message}"); }
    catch (Exception ex) { Console.WriteLine($"\n[ошибка] {ex.Message}"); }
}

cts.Cancel();

async static Task<bool> WaitForExtensionAsync(BridgeServer bridge, CancellationToken ct)
{
    try
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, ct);
        while (!bridge.IsClientConnected) { linked.Token.ThrowIfCancellationRequested(); await Task.Delay(300, linked.Token); }
        await Task.Delay(1000, linked.Token);
        return true;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[auto] расширение так и не подключилось");
        return false;
    }
}

async static Task RunAutoStepAsync(BridgeServer bridge, string op, Dictionary<string, object?>? args = null, bool printMarkdown = false)
{
    var sw = Stopwatch.StartNew();
    Console.WriteLine($"[auto] → {op}");
    try
    {
        var result = await bridge.RequestAsync(op, args);
        if (printMarkdown && result.TryGetProperty("markdown", out var md))
            Console.WriteLine($"--- markdown ---\n{md.GetString()}\n-----------------");
        else
            PrintResult(result);
        Console.WriteLine($"[auto] ← {op} за {sw.Elapsed.TotalSeconds:0.0}с");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[auto] {op} провалился: {ex.Message}");
    }
}

static void PrintResult(JsonElement result)
{
    var type = result.TryGetProperty("type", out var t) ? t.GetString() : null;
    if (type == "error")
    {
        var code = result.TryGetProperty("code", out var c) ? c.GetString() : "?";
        var msg = result.TryGetProperty("message", out var m) ? m.GetString() : "";
        Console.WriteLine($"\n[ошибка {code}] {msg}");
        if (result.TryGetProperty("data", out var d)) Console.WriteLine(d.GetRawText());
        return;
    }
    if (result.TryGetProperty("markdown", out var md))
        Console.WriteLine($"\n{md.GetString()}");
    else
        Console.WriteLine($"\n{result}");
}
