using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("DLSS_NEURAL_MIX_PORT"), out var port) && port > 0 && port <= 65535 ? port : 47837;
    private const long MaxPixels = 64_000_000;
    private static readonly string WorkRoot = Environment.GetEnvironmentVariable("DLSS_NEURAL_MIX_HOME") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLSSPhotoshopBridge");
    private static readonly RenderCache Frames = new(WorkRoot);
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static int rendering;
    private static string phase = "idle";
    private static AgentControl? agents;

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(WorkRoot);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); }
        catch (HttpListenerException error)
        {
            Log("Could not start listener: " + error);
            return 1;
        }
        agents = AgentControl.Create(WorkRoot);
        Log("Bridge ready on loopback port " + Port);
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception error) { Log("Listener stopped: " + error); return 1; }
            // Serve status requests while a render is running. Render itself
            // remains exclusive because it changes the neural runtime settings.
            _ = Task.Run(() => Handle(context));
        }
    }

    private static async Task Handle(HttpListenerContext context)
    {
        var response = context.Response;
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Width, X-Height";
        response.Headers["Access-Control-Expose-Headers"] = "X-DLSS-Bridge, X-DLSS-Cache";
        response.Headers["X-DLSS-Bridge"] = "4";
        try
        {
            var path = context.Request.Url?.AbsolutePath;
            if (path?.StartsWith("/agent/", StringComparison.Ordinal) == true)
            {
                await agents!.Handle(context, Volatile.Read(ref rendering) != 0);
                return;
            }
            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
            }
            else if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await WriteText(response, 200, "ready");
            }
            else if (context.Request.HttpMethod == "GET" && path == "/status")
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    busy = Volatile.Read(ref rendering) != 0,
                    phase = Volatile.Read(ref phase)
                });
                response.StatusCode = 200;
                response.ContentType = "application/json";
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, 0, body.Length);
            }
            else if (context.Request.HttpMethod == "POST" && path == "/player-cache/clear")
            {
                await ClearCache(response, ClearPlayerCache, RenderGate);
            }
            else if (context.Request.HttpMethod == "POST" && path == "/render")
            {
                // A client can receive the final RGB byte before this request
                // finishes cleanup. Allow that short handoff to finish instead
                // of rejecting the next panorama view or cached Mix request.
                if (!await RenderGate.WaitAsync(TimeSpan.FromSeconds(2)))
                    await WriteText(response, 409, "The renderer is already processing another image.");
                else
                {
                    Volatile.Write(ref rendering, 1);
                    SetPhase("receiving");
                    try { await Render(context); }
                    catch { SetPhase("failed"); throw; }
                    finally { Volatile.Write(ref rendering, 0); RenderGate.Release(); }
                }
            }
            else await WriteText(response, 404, "Unknown bridge endpoint.");
        }
        catch (Exception error)
        {
            Log("Bridge request failed: " + error);
            if (response.OutputStream.CanWrite)
            {
                try { await WriteText(response, 500, error.Message); }
                catch { /* The client disconnected. */ }
            }
        }
        finally { try { response.Close(); } catch { } }
    }

    internal sealed record PlayerCacheResult(long Bytes, string Root);

    internal static async Task ClearCache(HttpListenerResponse response, Func<Task<PlayerCacheResult>> clear, SemaphoreSlim gate)
    {
        // Share the render lock: never delete a frame being sent or cached,
        // including when another panel is using the bridge.
        if (!await gate.WaitAsync(0))
        {
            await WriteText(response, 409, "An image is still processing. Wait until it finishes, then clear the cache.");
            return;
        }
        try
        {
            var cleared = await clear();
            var body = JsonSerializer.SerializeToUtf8Bytes(new { bytes = cleared.Bytes, root = cleared.Root });
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
        }
        finally { gate.Release(); }
    }

    private static async Task<PlayerCacheResult> ClearPlayerCache()
    {
        var player = ConfigValue("playerExePath");
        if (string.IsNullOrWhiteSpace(player))
            throw new IOException("The DLSS Video Player location is not saved. Close Photoshop and reinstall DLSS Neural Mix, selecting the player's EXE.");
        var runner = RunnerPath();
        var result = await RunProcess(runner, "--clear-player-cache " + Quote(player),
            Path.GetDirectoryName(runner) ?? AppContext.BaseDirectory);
        return JsonSerializer.Deserialize<PlayerCacheResult>(result,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("The player cache helper returned no result.");
    }

    private static async Task Render(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (!int.TryParse(request.Headers["X-Width"], out var width) ||
            !int.TryParse(request.Headers["X-Height"], out var height) ||
            width < 64 || height < 64 || width > 8192 || height > 8192 ||
            (long)width * height > MaxPixels)
        {
            await WriteText(response, 400, "Image dimensions must be 64–8192 pixels on each side and at most 64 megapixels.");
            return;
        }
        var inputBytes = checked((long)width * height * 3);
        if (request.ContentLength64 >= 0 && request.ContentLength64 != inputBytes)
        {
            await WriteText(response, 400, "RGB input length does not match the image dimensions.");
            return;
        }

        var runner = RunnerPath();
        var runnerDirectory = Path.GetDirectoryName(runner);
        if (string.IsNullOrEmpty(runnerDirectory))
            throw new FileNotFoundException("The Photoshop neural runner is not configured. Reinstall DLSS Neural Mix.");
        var ffmpeg = Path.Combine(runnerDirectory, "ffmpeg.exe");
        var ffprobe = Path.Combine(runnerDirectory, "ffprobe.exe");
        var worker = Path.Combine(runnerDirectory, "neural-runtime", "NeuralWorker.exe");
        if (!File.Exists(runner) || !File.Exists(ffmpeg) || !File.Exists(ffprobe) || !File.Exists(worker))
            throw new FileNotFoundException("The Photoshop neural runner, worker, or media tools are missing. Reinstall DLSS Neural Mix.");
        var job = Path.Combine(WorkRoot, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var input = Path.Combine(job, "input.bmp");
            var output = Path.Combine(job, "output.mkv");
            var raw = Path.Combine(job, "output.rgb");
            var digest = await WriteBmp(request.InputStream, input, width, height);
            SetPhase("checkingCache");
            var settingsPath = Path.Combine(runnerDirectory, "neural-runtime", "ReShade.ini");
            string CacheKey() => digest + ":" + width + "x" + height + ":" + runner + ":" +
                File.GetLastWriteTimeUtc(runner).Ticks + ":" +
                File.GetLastWriteTimeUtc(worker).Ticks + ":" +
                RenderCache.SettingsFingerprint(settingsPath);
            var cached = Frames.TryGet(CacheKey(), inputBytes);
            if (cached != null)
            {
                response.Headers["X-DLSS-Cache"] = "hit";
                SetPhase("sending");
                await SendRgb(response, cached, inputBytes);
                SetPhase("complete");
                return;
            }

            Log($"Rendering {width}x{height}");
            SetPhase("rendering");
            await RunProcess(runner,
                $"--input {Quote(input)} --output {Quote(output)} --width {width} --height {height}",
                runnerDirectory);
            if (!File.Exists(output)) throw new IOException("The neural worker produced no output frame.");
            SetPhase("decoding");
            await RunProcess(ffmpeg,
                $"-hide_banner -nostdin -loglevel error -y -i {Quote(output)} -frames:v 1 -f rawvideo -pix_fmt rgb24 {Quote(raw)}",
                runnerDirectory);
            if (new FileInfo(raw).Length != inputBytes)
                throw new IOException("The decoded neural image has the wrong dimensions.");
            // The runner can rewrite ReShade.ini without changing its contents.
            // Fingerprint the resulting settings to retain both panorama views.
            var cache = Frames.Store(CacheKey(), raw, inputBytes);
            response.Headers["X-DLSS-Cache"] = "miss";
            SetPhase("sending");
            await SendRgb(response, cache, inputBytes);
            SetPhase("complete");
            Log($"Completed {width}x{height}");
        }
        finally { try { Directory.Delete(job, true); } catch { } }
    }

    private static async Task<string> WriteBmp(Stream source, string path, int width, int height)
    {
        var rowBytes = checked(width * 3);
        var stride = (rowBytes + 3) & ~3;
        var imageBytes = checked(stride * height);
        using var destination = File.Create(path);
        using var writer = new BinaryWriter(destination, Encoding.ASCII, true);
        writer.Write((ushort)0x4D42);
        writer.Write(checked(54 + imageBytes));
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height); // top-down rows, matching Photoshop's buffer
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var rgb = new byte[rowBytes];
        var bgr = new byte[stride];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var y = 0; y < height; y++)
        {
            var filled = 0;
            while (filled < rgb.Length)
            {
                var count = await source.ReadAsync(rgb, filled, rgb.Length - filled);
                if (count == 0) throw new EndOfStreamException("The RGB request ended early.");
                filled += count;
            }
            hash.AppendData(rgb);
            for (var x = 0; x < rowBytes; x += 3)
            {
                bgr[x] = rgb[x + 2]; bgr[x + 1] = rgb[x + 1]; bgr[x + 2] = rgb[x];
            }
            await destination.WriteAsync(bgr, 0, bgr.Length);
        }
        if (source.ReadByte() != -1) throw new IOException("The RGB request contained extra bytes.");
        return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "");
    }

    private static async Task<string> RunProcess(string executable, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (!process.Start()) throw new IOException("Could not start " + Path.GetFileName(executable));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600000))
        {
            process.Kill();
            throw new TimeoutException(Path.GetFileName(executable) + " exceeded ten minutes.");
        }
        var details = (await stdout) + (await stderr);
        if (process.ExitCode != 0)
            throw new IOException(Path.GetFileName(executable) + " failed (" + process.ExitCode + "): " + details.Trim());
        return await stdout;
    }

    private static async Task SendRgb(HttpListenerResponse response, string path, long length)
    {
        response.StatusCode = 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = length;
        using var file = File.OpenRead(path);
        await file.CopyToAsync(response.OutputStream);
    }

    private static async Task WriteText(HttpListenerResponse response, int code, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        response.StatusCode = code;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static string RunnerPath() => ConfigValue("runnerPath") ??
        Environment.GetEnvironmentVariable("DLSS_PHOTOSHOP_RUNNER") ?? "";

    private static string? ConfigValue(string name)
    {
        // The installer writes a per-user path. Development builds can use a
        // config beside this executable instead.
        var configs = new[]
        {
            Path.Combine(WorkRoot, "bridge-config.json"),
            Path.Combine(AppContext.BaseDirectory, "bridge-config.json")
        };
        foreach (var config in configs)
            if (File.Exists(config))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config));
                return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
            }
        return null;
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    private static void SetPhase(string value) => Volatile.Write(ref phase, value);
    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(WorkRoot, "bridge.log"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine); }
        catch { }
    }
}
