using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<NeuralTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class NeuralTools
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var address = Environment.GetEnvironmentVariable("DLSS_NEURAL_MIX_BRIDGE_URL") ?? "http://127.0.0.1:47837/";
        var uri = new Uri(address);
        if (uri.Scheme != "http" || !uri.IsLoopback) throw new InvalidOperationException("The DLSS bridge must be local loopback HTTP.");
        return new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
    }
    private static async Task<JsonElement> Request(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var tokenPath = Environment.GetEnvironmentVariable("DLSS_NEURAL_MIX_TOKEN_FILE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSSPhotoshopBridge", "automation-token.txt");
        if (!File.Exists(tokenPath)) throw new McpException("Open Photoshop > Plugins > DLSS Neural Mix and wait for Agent control connected.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-DLSS-Agent", "1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim());
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client.SendAsync(request, cancellationToken);
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new McpException(data.TryGetProperty("error", out var error) ? error.GetString() ?? "Bridge rejected the command." : "Bridge rejected the command.");
            return data;
        }
        catch (HttpRequestException) { throw new McpException("The local DLSS bridge is unavailable. Open the updated DLSS Neural Mix panel in Photoshop."); }
    }
    [McpServerTool(Name = "dlss_apply", Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Apply DLSS Neural Mix to exactly one selected Photoshop layer. Photoshop and the v0.2.15+ DLSS Neural Mix panel must be loaded. Creates a new Smart Object and preserves the source. At most one Apply is accepted. Returns completion and document/source/result layer IDs, or a running job ID after 25 seconds. If still running, use dlss_status with that job ID; never repeat Apply to wait for it. This uses the existing renderer; its high-resolution brightness limitation remains.")]
    public static async Task<string> Apply(
        [Description("Neural Mix percentage, integer 0–200."), Range(0, 200)] int mix,
        [Description("True for a 360 panorama with horizontal seam protection; false for an ordinary image.")] bool pano,
        CancellationToken cancellationToken)
    {
        if (mix < 0 || mix > 200) throw new McpException("Mix must be an integer between 0 and 200.");
        var id = Guid.NewGuid().ToString("N");
        JsonElement job;
        try { job = await Request(HttpMethod.Post, "agent/apply", new { requestId = id, mix, pano }, cancellationToken); }
        catch (OperationCanceledException) { throw new McpException($"Apply submission was interrupted. Its job ID is {id}. Check dlss_status before retrying; the command may already have been accepted."); }
        catch (McpException error) { throw new McpException($"Apply submission for job {id}: {error.Message} If the connection failed after submission, check dlss_status with this ID before retrying."); }
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (job.GetProperty("status").GetString() is "queued" or "running" && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(500, cancellationToken);
                job = await Request(HttpMethod.Get, "agent/jobs/" + id, null, cancellationToken);
            }
            catch (Exception error) when (error is OperationCanceledException or McpException)
            { throw new McpException($"Apply job {id} may still be processing. Use dlss_status with that ID before another Apply. {error.Message}"); }
        }
        if (job.GetProperty("status").GetString() == "failed")
            throw new McpException($"Apply job {id} failed: {job.GetProperty("error").GetString()}");
        return job.GetRawText();
    }
    [McpServerTool(Name = "dlss_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Read the DLSS Neural Mix Photoshop panel connection, busy state and selected layer, or the completion/error/result layer IDs of an Apply job. Use this to wait for a running job without creating another Smart Object. A disconnected/unknown job is not evidence that Apply failed.")]
    public static async Task<string> Status(
        [Description("Job ID returned by dlss_apply; omit to read current Photoshop panel state.")] string? job_id = null,
        CancellationToken cancellationToken = default)
    {
        if (job_id != null && !Guid.TryParse(job_id, out _)) throw new McpException("Use the job ID returned by dlss_apply.");
        var data = await Request(HttpMethod.Get, job_id == null ? "agent/state?fresh=1" : "agent/jobs/" + job_id, null, cancellationToken);
        return data.GetRawText();
    }
}
