using System.Net;
using System.Text.Json;

/// <summary>Opt-in fixture; never records tokens, request bodies, or device identifiers.</summary>
public sealed class DeterministicSimulator : IDisposable
{
    private readonly object gate = new();
    private string status = "closed";
    private int startDelayMs = 8000, motionMs = 10000;
    private long generation;
    private CancellationTokenSource transition = new();
    private readonly Dictionary<string, int> requests = new();
    private readonly Dictionary<string, int> authorized = new();
    private readonly Dictionary<string, Queue<ResponseScript>> scripts = new();
    private readonly List<string> commands = new();
    private static readonly string[] Endpoints = { "devices", "status", "commands", "ping" };

    public sealed record ResponseScript(string Endpoint, int DelayMs = 0, int? StatusCode = null, string? Body = null);
    public sealed record ResetOptions(string Status = "closed", int StartDelayMs = 8000, int MotionMs = 10000);

    public static void Install(WebApplication app, int controlPort)
    {
        var state = new DeterministicSimulator();
        app.Lifetime.ApplicationStopping.Register(state.Dispose);
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/__test", StringComparison.Ordinal))
            {
                var ip = context.Connection.RemoteIpAddress;
                if (context.Connection.LocalPort != controlPort || ip == null ||
                    !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                await state.Control(context);
                return;
            }
            // Do not expose API traffic on the unencrypted control port.
            if (context.Connection.LocalPort == controlPort)
            {
                context.Response.StatusCode = 404;
                return;
            }
            var endpoint = path == "/v1/devices" ? "devices" : path == "/ping" ? "ping" :
                path.StartsWith("/v1/devices/") && path.EndsWith("/status") ? "status" :
                path.StartsWith("/v1/devices/") && path.EndsWith("/commands") ? "commands" : null;
            if (endpoint == null) { await next(context); return; }
            var expectedMethod = endpoint is "commands" or "ping" ? "POST" : "GET";
            if (context.Request.Method != expectedMethod) { context.Response.StatusCode = 405; return; }
            long requestGeneration;
            ResponseScript? script;
            lock (state.gate)
            {
                requestGeneration = state.generation;
                state.requests[endpoint] = state.requests.GetValueOrDefault(endpoint) + 1;
                if (context.Request.Headers.ContainsKey("Authorization"))
                    state.authorized[endpoint] = state.authorized.GetValueOrDefault(endpoint) + 1;
                script = state.scripts.TryGetValue(endpoint, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
            }
            if (script?.DelayMs > 0) await Task.Delay(script.DelayMs, context.RequestAborted);
            lock (state.gate)
            {
                if (requestGeneration != state.generation) { context.Response.StatusCode = 409; return; }
            }
            if (script?.StatusCode != null || script?.Body != null)
            {
                context.Response.StatusCode = script.StatusCode ?? 200;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(script.Body ?? "{}", context.RequestAborted);
                return;
            }
            if (endpoint == "devices") { await next(context); return; }
            if (endpoint == "ping")
            {
                await context.Response.WriteAsJsonAsync(new { message = "ok" }, context.RequestAborted);
                return;
            }
            if (endpoint == "status")
            {
                string value;
                lock (state.gate) value = state.status;
                await context.Response.WriteAsJsonAsync(new { components = new { main = new { doorControl = new { door = new { value } } } } }, context.RequestAborted);
                return;
            }
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var items = body.RootElement.GetProperty("commands");
                if (items.GetArrayLength() != 1) { context.Response.StatusCode = 400; return; }
                var command = items[0].GetProperty("command").GetString()?.ToLowerInvariant();
                if (command != "open" && command != "close") { context.Response.StatusCode = 400; return; }
                if (!state.Accept(command, requestGeneration)) { context.Response.StatusCode = 409; return; }
                await context.Response.WriteAsJsonAsync(new { results = new[] { new { status = "ACCEPTED" } } }, context.RequestAborted);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { context.Response.StatusCode = 400; }
        });
    }

    private bool Accept(string command, long requestGeneration)
    {
        lock (gate)
        {
            if (requestGeneration != generation) return false;
            transition.Cancel();
            transition.Dispose();
            transition = new CancellationTokenSource();
            commands.Add(command);
            status = command == "open" ? "closed" : "open";
            _ = Move(command, generation, transition.Token, startDelayMs, motionMs);
            return true;
        }
    }

    private async Task Move(string command, long operationGeneration, CancellationToken token, int delay, int motion)
    {
        try
        {
            await Task.Delay(delay, token);
            lock (gate)
            {
                if (token.IsCancellationRequested || operationGeneration != generation) return;
                status = command == "open" ? "opening" : "closing";
            }
            await Task.Delay(motion, token);
            lock (gate)
            {
                if (token.IsCancellationRequested || operationGeneration != generation) return;
                status = command == "open" ? "open" : "closed";
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Control(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path == "/__test/snapshot" && context.Request.Method == "GET")
        {
            object snapshot;
            lock (gate) snapshot = new { status, generation, startDelayMs, motionMs,
                requests = new Dictionary<string, int>(requests),
                authorizationPresent = new Dictionary<string, int>(authorized),
                acceptedCommands = commands.ToArray() };
            await context.Response.WriteAsJsonAsync(snapshot, context.RequestAborted);
            return;
        }
        try
        {
            if (path == "/__test/reset" && context.Request.Method == "POST")
            {
                var options = await context.Request.ReadFromJsonAsync<ResetOptions>(context.RequestAborted);
                if (options == null || !new[] { "closed", "open", "opening", "closing", "unknown" }.Contains(options.Status) ||
                    options.StartDelayMs < 0 || options.StartDelayMs > 120000 || options.MotionMs < 0 || options.MotionMs > 120000)
                { context.Response.StatusCode = 400; return; }
                lock (gate)
                {
                    generation++;
                    transition.Cancel();
                    transition.Dispose();
                    transition = new CancellationTokenSource();
                    status = options.Status;
                    startDelayMs = options.StartDelayMs;
                    motionMs = options.MotionMs;
                    requests.Clear(); authorized.Clear(); scripts.Clear(); commands.Clear();
                }
                await context.Response.WriteAsJsonAsync(new { reset = true }, context.RequestAborted);
                return;
            }
            if (path == "/__test/script" && context.Request.Method == "POST")
            {
                var script = await context.Request.ReadFromJsonAsync<ResponseScript>(context.RequestAborted);
                if (script == null || !Endpoints.Contains(script.Endpoint) || script.DelayMs < 0 || script.DelayMs > 120000 ||
                    (script.StatusCode != null && (script.StatusCode < 100 || script.StatusCode > 599)) ||
                    script.Body?.Length > 65536)
                { context.Response.StatusCode = 400; return; }
                lock (gate)
                {
                    if (!scripts.TryGetValue(script.Endpoint, out var queue)) scripts[script.Endpoint] = queue = new();
                    if (queue.Count >= 100) { context.Response.StatusCode = 409; return; }
                    queue.Enqueue(script);
                }
                await context.Response.WriteAsJsonAsync(new { queued = true }, context.RequestAborted);
                return;
            }
            context.Response.StatusCode = 404;
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        { context.Response.StatusCode = 400; }
    }

    public void Dispose()
    {
        lock (gate) { generation++; transition.Cancel(); }
    }
}
