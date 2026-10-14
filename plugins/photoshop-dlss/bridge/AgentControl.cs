using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// Commands contain settings only. Photoshop reads and changes the selected
// layer in the panel's existing modal workflow; no scripts or paths are accepted.
internal sealed class AgentControl
{
    internal sealed record PanelState(bool Busy, int? DocumentId, int? LayerId, int SelectedLayers, int Mix, bool Pano, string Version);
    internal sealed record Completion(string Id, JsonElement Result);
    internal sealed record PollMessage([property: JsonRequired] string Session, [property: JsonRequired] PanelState State, Completion? Completion = null, bool Wait = true);
    internal sealed record ApplyRequest([property: JsonRequired] string RequestId, [property: JsonRequired] int Mix, [property: JsonRequired] bool Pano);
    internal sealed record JobView(string Id, int Mix, bool Pano, string Status, JsonElement? Result, string? Error);
    internal sealed class ApiError(int status, string message) : Exception(message) { public int Status { get; } = status; }
    private sealed class Job(ApplyRequest request, DateTimeOffset now)
    {
        public ApplyRequest Request = request;
        public DateTimeOffset Created = now;
        public string Status = "queued";
        public JsonElement? Result;
        public string? Error;
        public JobView View() => new(Request.RequestId, Request.Mix, Request.Pano, Status, Result, Error);
    }
    private readonly object sync = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly Dictionary<string, Job> jobs = new();
    private readonly Queue<string> history = new();
    private readonly Func<DateTimeOffset> clock;
    private TaskCompletionSource stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? instance, session;
    private DateTimeOffset seen;
    private PanelState? state;
    private Job? active;
    private readonly string token;
    internal AgentControl(string token, Func<DateTimeOffset>? clock = null)
    { this.token = token; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }
    internal static AgentControl Create(string root)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(Path.Combine(root, "automation-token.txt"), token);
        return new AgentControl(token);
    }
    private bool Online => session != null && clock() - seen < TimeSpan.FromSeconds(45);
    private void Expire()
    {
        if (active?.Status == "queued" && (!Online || clock() - active.Created > TimeSpan.FromSeconds(30)))
        { active.Status = "failed"; active.Error = "Photoshop panel disconnected before Apply. Nothing was processed."; active = null; }
        // A lost panel after dispatch is not proof of failure. Keep the job
        // reserved until that panel reports its outcome; never retry Apply.
        if (active?.Status == "running" && !Online) active.Status = "unknown";
    }
    internal object Connect(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ApiError(400, "Invalid panel instance.");
        lock (sync)
        {
            Expire();
            if (instance != id && (Online || active != null))
                throw new ApiError(409, "Another Photoshop panel owns agent control. Close it before connecting this panel.");
            if (instance != id || session == null) { instance = id; session = Guid.NewGuid().ToString("N"); state = null; }
            seen = clock();
            return new { token, session };
        }
    }
    internal JobView Submit(ApplyRequest request, bool rendererBusy)
    {
        if (!Guid.TryParse(request.RequestId, out _) || request.Mix < 0 || request.Mix > 200)
            throw new ApiError(400, "Mix must be an integer from 0 to 200; requestId must be a UUID.");
        lock (sync)
        {
            Expire();
            if (jobs.TryGetValue(request.RequestId, out var existing))
            {
                if (existing.Request != request) throw new ApiError(409, "That requestId was already used with different settings.");
                return existing.View();
            }
            if (!Online || state == null) throw new ApiError(503, "Open Photoshop > Plugins > DLSS Neural Mix and wait for Agent control connected.");
            if (rendererBusy || state.Busy || active != null) throw new ApiError(409, "Photoshop or the renderer is busy. Check the existing job; do not submit another Apply.");
            if (state.SelectedLayers != 1 || state.DocumentId == null || state.LayerId == null)
                throw new ApiError(409, "Open a document and select exactly one layer in Photoshop.");
            active = new Job(request, clock());
            jobs.Add(request.RequestId, active); history.Enqueue(request.RequestId);
            while (history.Count > 64) jobs.Remove(history.Dequeue());
            if (changed.CurrentCount == 0) changed.Release();
            return active.View();
        }
    }
    internal object Snapshot()
    {
        lock (sync) { Expire(); return new { connected = Online && state != null, state, job = active?.View() }; }
    }
    private void WakePanel() { if (changed.CurrentCount == 0) changed.Release(); }
    private async Task RefreshSelection()
    {
        Task refreshed;
        lock (sync)
        {
            if (!Online || state?.Busy == true || active != null) return;
            refreshed = stateChanged.Task;
            WakePanel();
        }
        try { await refreshed.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new ApiError(503, "The Photoshop panel did not respond. Keep it loaded and retry after checking its activity log."); }
    }
    internal JobView GetJob(string id)
    {
        lock (sync) { Expire(); return jobs.TryGetValue(id, out var job) ? job.View() : throw new ApiError(404, "Unknown job ID. Do not assume Apply completed."); }
    }
    internal async Task<object> Poll(PollMessage message, TimeSpan? wait = null)
    {
        void Update()
        {
            if (message.State == null) throw new ApiError(400, "Missing Photoshop panel state.");
            if (message.Session != session) throw new ApiError(409, "Panel session expired. Reconnect before sending commands.");
            seen = clock(); state = message.State;
            stateChanged.TrySetResult();
            stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (message.Completion != null)
            {
                if (!jobs.TryGetValue(message.Completion.Id, out var finished)) throw new ApiError(404, "Unknown completion ID.");
                if (finished.Status == "queued") throw new ApiError(409, "An undispatched job cannot complete.");
                if (finished.Result == null)
                {
                    var result = message.Completion.Result;
                    if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out var ok) ||
                        ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiError(400, "Invalid completion.");
                    finished.Result = result.Clone(); finished.Status = ok.GetBoolean() ? "completed" : "failed";
                    if (!ok.GetBoolean()) finished.Error = result.TryGetProperty("error", out var error) ? error.GetString() : "Photoshop Apply failed.";
                    if (active == finished) active = null;
                }
            }
        }
        JobView? Dispatch()
        {
            Expire();
            if (!message.State.Busy && active?.Status == "queued") { active.Status = "running"; return active.View(); }
            return null;
        }
        JobView? next;
        lock (sync) { Update(); next = Dispatch(); }
        if (next == null && message.Wait)
        {
            await changed.WaitAsync(wait ?? TimeSpan.FromSeconds(15));
            lock (sync)
            {
                if (message.Session != session) throw new ApiError(409, "Panel session expired.");
                seen = clock(); next = Dispatch();
            }
        }
        return new { acknowledged = message.Completion?.Id, job = next };
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static async Task<T> Body<T>(HttpListenerRequest request)
    {
        if (request.ContentLength64 < 0 || request.ContentLength64 > 16384) throw new ApiError(413, "Agent JSON requests must be at most 16 KiB with a Content-Length.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await JsonSerializer.DeserializeAsync<T>(request.InputStream, Json, timeout.Token) ?? throw new ApiError(400, "Missing JSON body."); }
        catch (JsonException) { throw new ApiError(400, "Invalid JSON settings."); }
        catch (OperationCanceledException) { throw new ApiError(408, "Agent request body timed out."); }
    }
    internal async Task Handle(HttpListenerContext context, bool rendererBusy)
    {
        var request = context.Request; var response = context.Response;
        // UXP and the stdio adapter are native clients. Browser requests cannot
        // bootstrap a token or mutate Photoshop through wildcard render CORS.
        foreach (var header in new[] { "Access-Control-Allow-Origin", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers" }) response.Headers.Remove(header);
        object result;
        try
        {
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Url == null || request.Url.Host is not ("localhost" or "127.0.0.1") ||
                request.Headers["Origin"] != null || request.Headers["X-DLSS-Agent"] != "1")
                throw new ApiError(403, "Agent control accepts native loopback clients only.");
            var path = request.Url.AbsolutePath;
            if (request.HttpMethod == "POST" && path == "/agent/connect")
            { var body = await Body<JsonElement>(request); result = Connect(body.GetProperty("instance").GetString() ?? ""); }
            else
            {
                if (request.Headers["Authorization"] != "Bearer " + token) throw new ApiError(401, "Invalid local agent token. Reconnect the Photoshop panel.");
                if (request.HttpMethod == "POST" && path == "/agent/poll") result = await Poll(await Body<PollMessage>(request));
                else if (request.HttpMethod == "POST" && path == "/agent/apply")
                { var apply = await Body<ApplyRequest>(request); await RefreshSelection(); result = Submit(apply, rendererBusy); }
                else if (request.HttpMethod == "GET" && path == "/agent/state")
                { if (request.QueryString["fresh"] == "1") await RefreshSelection(); result = Snapshot(); }
                else if (request.HttpMethod == "GET" && path.StartsWith("/agent/jobs/", StringComparison.Ordinal)) result = GetJob(path[12..]);
                else throw new ApiError(404, "Unknown agent endpoint.");
            }
            response.StatusCode = 200;
        }
        catch (ApiError error) { response.StatusCode = error.Status; result = new { error = error.Message }; }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or ArgumentException)
        { response.StatusCode = 400; result = new { error = "Invalid agent request." }; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        response.ContentType = "application/json"; response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
