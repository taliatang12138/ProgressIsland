using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class LocalApi(TaskStore store, Settings settings) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connections = new(12);
    private TcpListener? listener;
    public string? Error { get; private set; }

    public void Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, settings.Port);
            listener.Start(20);
            _ = AcceptAsync();
        }
        catch (SocketException ex) { Error = $"本机接口端口 {settings.Port} 不可用：{ex.Message}"; }
    }
    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await connections.WaitAsync(lifetime.Token);
                TcpClient client;
                try { client = await listener!.AcceptTcpClientAsync(lifetime.Token); }
                catch { connections.Release(); throw; }
                _ = HandleAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
        }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream();
                var headerBytes = new List<byte>(512);
                var one = new byte[1];
                while (headerBytes.Count < 16384)
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) return;
                    headerBytes.Add(one[0]);
                    var n = headerBytes.Count;
                    if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                }
                if (headerBytes.Count >= 16384) { await Reply(stream, 413, new { error = "请求头过长" }, timeout.Token); return; }
                var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length != 3) { await Reply(stream, 400, new { error = "请求格式无效" }, timeout.Token); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0 && !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                    { await Reply(stream, 400, new { error = "重复请求头" }, timeout.Token); return; }
                }
                if (headers.ContainsKey("Transfer-Encoding"))
                { await Reply(stream, 400, new { error = "不支持分块请求" }, timeout.Token); return; }
                var length = 0;
                if (headers.TryGetValue("Content-Length", out var size) && (!int.TryParse(size, out length) || length < 0 || length > 65536))
                { await Reply(stream, 413, new { error = "请求体最大 64 KB" }, timeout.Token); return; }
                if (length > 0 && headers.GetValueOrDefault("Expect", "").Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), timeout.Token);
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token);
                // Consume the bounded body before rejection; otherwise close may reset the response on Windows.
                var provided = headers.GetValueOrDefault("Authorization", "");
                var expected = "Bearer " + settings.Token;
                if (headers.ContainsKey("Origin") || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
                { await Reply(stream, 401, new { error = "需要本机访问令牌" }, timeout.Token); return; }
                var method = first[0]; var path = first[1];
                if (method == "GET" && path == "/health") await Reply(stream, 200, new { app = "ProgressIsland", version = typeof(App).Assembly.GetName().Version?.ToString(3) }, timeout.Token);
                else if (method == "GET" && path == "/tasks") await Reply(stream, 200, store.Snapshot(), timeout.Token);
                else if (method == "POST" && path == "/tasks")
                {
                    var update = JsonSerializer.Deserialize<TaskUpdate>(body, TaskStore.Json) ?? throw new ArgumentException("缺少任务信息");
                    // The HTTP interface cannot launch programs, attach arbitrary PIDs, or read files.
                    if (update.Source is not null and not "api" || update.ProcessId is not null || update.LogPath is not null)
                        throw new ArgumentException("接口只接受进度上报，不能启动或监控程序/文件");
                    if (update.Id is { } id && store.Find(id) is { Source: not "api" })
                        throw new ArgumentException("不能通过接口修改其他来源的任务");
                    var item = store.Update(update with { Source = "api", ActivityAt = DateTimeOffset.UtcNow });
                    await Reply(stream, 200, item, timeout.Token);
                }
                else await Reply(stream, 404, new { error = "接口不存在" }, timeout.Token);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                try { await Reply(client.GetStream(), 400, new { error = ex.Message }, timeout.Token); } catch { }
            }
            catch (Exception) { /* A malformed or disconnected local client must not stop the server. */ }
            finally { connections.Release(); }
        }
    }
    private static async Task Reply(NetworkStream stream, int status, object data, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(data, TaskStore.Json);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }
    public void Dispose() { lifetime.Cancel(); listener?.Stop(); }
}
