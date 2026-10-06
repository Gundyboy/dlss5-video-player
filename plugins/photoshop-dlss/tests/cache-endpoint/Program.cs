using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class CacheEndpointTests
{
    private static async Task Main()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var fixture = Path.Combine(tempRoot, "dlss-clear-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            // Exercise the actual HTTP response with a private fixture cache
            // and render gate; never start the installed Photoshop bridge.
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            using var listener = new HttpListener();
            var endpoint = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(endpoint);
            listener.Start();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var gate = new SemaphoreSlim(1, 1);
            var cache = new RenderCache(fixture);
            var raw = Path.Combine(fixture, "input.raw");
            File.WriteAllBytes(raw, new byte[] { 1, 2, 3 });
            var slot = cache.Store("source", raw, 3);
            var settings = Path.Combine(fixture, "bridge-config.json");
            File.WriteAllText(settings, "keep");

            async Task<HttpResponseMessage> Clear()
            {
                var responseTask = client.PostAsync(endpoint + "cache/clear", null);
                var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
                try { await Program.ClearCache(request.Response, cache, gate); }
                finally { request.Response.Close(); }
                return await responseTask;
            }

            await gate.WaitAsync();
            using (var blocked = await Clear())
            {
                Check(blocked.StatusCode == HttpStatusCode.Conflict, "A busy renderer must return HTTP 409.");
                Check(File.Exists(slot) && cache.TryGet("source", 3) != null, "A busy clear must retain the disk frame and its key.");
                Check(gate.CurrentCount == 0, "A rejected clear must not release the renderer's lock.");
            }
            gate.Release();
            using (var cleared = await Clear())
            {
                Check(cleared.StatusCode == HttpStatusCode.OK, "Idle cache clearing must succeed.");
                using var body = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("files").GetInt32() == 1 &&
                    body.RootElement.GetProperty("bytes").GetInt64() == 3, "The API must report exact cleared files and bytes.");
                Check(!File.Exists(slot) && File.ReadAllText(settings) == "keep", "Only owned render cache files may be removed.");
                Check(gate.CurrentCount == 1, "The clear must release its lock.");
            }
            using (var empty = await Clear())
            {
                using var body = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("files").GetInt32() == 0, "Repeated clearing must report an empty cache.");
            }
            Console.WriteLine("Cache HTTP response, busy renderer protection, retained settings, and repeated clearing passed.");
        }
        finally
        {
            var absolute = Path.GetFullPath(fixture);
            if (!absolute.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith("dlss-clear-cache-test-", StringComparison.Ordinal))
                throw new Exception("Unsafe fixture cleanup path.");
            Directory.Delete(absolute, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
