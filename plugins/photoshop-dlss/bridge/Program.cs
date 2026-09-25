using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

internal static class Program
{
    private const int Port = 47837;
    private const long MaxPixels = 64_000_000;
    private static readonly string WorkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLSSPhotoshopBridge");
    private static string? lastKey;
    private static string? lastRgb;

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
        Log("Bridge ready on loopback port " + Port);
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception error) { Log("Listener stopped: " + error); return 1; }
            // One render at a time: the neural runtime is exclusive and its
            // ReShade settings must not be changed by a concurrent job.
            await Handle(context);
        }
    }

    private static async Task Handle(HttpListenerContext context)
    {
        var response = context.Response;
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Width, X-Height";
        response.Headers["X-DLSS-Bridge"] = "1";
        try
        {
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
            }
            else if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await WriteText(response, 200, "ready");
            }
            else if (context.Request.HttpMethod == "POST" && path == "/render")
            {
                await Render(context);
            }
            else await WriteText(response, 404, "Unknown bridge endpoint.");
        }
        catch (Exception error)
        {
            Log("Render failed: " + error);
            if (response.OutputStream.CanWrite)
            {
                try { await WriteText(response, 500, error.Message); }
                catch { /* The client disconnected. */ }
            }
        }
        finally { try { response.Close(); } catch { } }
    }

    private static async Task Render(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (!int.TryParse(request.Headers["X-Width"], out var width) ||
            !int.TryParse(request.Headers["X-Height"], out var height) ||
            width < 64 || height < 64 || width > 16384 || height > 16384 ||
            (long)width * height > MaxPixels)
        {
            await WriteText(response, 400, "Invalid image dimensions.");
            return;
        }
        var inputBytes = checked((long)width * height * 3);
        if (request.ContentLength64 >= 0 && request.ContentLength64 != inputBytes)
        {
            await WriteText(response, 400, "RGB input length does not match the image dimensions.");
            return;
        }

        var player = PlayerPath();
        var ffmpeg = Path.Combine(Path.GetDirectoryName(player)!, "ffmpeg.exe");
        if (!File.Exists(player) || !File.Exists(ffmpeg))
            throw new FileNotFoundException("The patched player or ffmpeg.exe is missing from the bridge configuration.");
        var job = Path.Combine(WorkRoot, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var input = Path.Combine(job, "input.bmp");
            var output = Path.Combine(job, "output.png");
            var raw = Path.Combine(job, "output.rgb");
            var digest = await WriteBmp(request.InputStream, input, width, height);
            var playerDirectory = Path.GetDirectoryName(player)!;
            var settingsPath = Path.Combine(playerDirectory, "neural-runtime", "ReShade.ini");
            var playerSettingsPath = Path.Combine(playerDirectory, "DLSSVideoPlayer.ini");
            string CacheKey() => digest + ":" + width + "x" + height + ":" +
                File.GetLastWriteTimeUtc(player).Ticks + ":" +
                File.GetLastWriteTimeUtc(settingsPath).Ticks + ":" +
                File.GetLastWriteTimeUtc(playerSettingsPath).Ticks;
            if (CacheKey() == lastKey && lastRgb != null && File.Exists(lastRgb))
            {
                await SendRgb(response, lastRgb, inputBytes);
                return;
            }

            Log($"Rendering {width}x{height}");
            await RunProcess(player,
                $"--render {Quote(input)} --stages nr --out {Quote(output)} --quiet",
                Path.GetDirectoryName(player)!);
            if (!File.Exists(output)) throw new IOException("The player produced no neural PNG.");
            await RunProcess(ffmpeg,
                $"-hide_banner -nostdin -loglevel error -y -i {Quote(output)} -frames:v 1 -f rawvideo -pix_fmt rgb24 {Quote(raw)}",
                Path.GetDirectoryName(player)!);
            if (new FileInfo(raw).Length != inputBytes)
                throw new IOException("The decoded neural image has the wrong dimensions.");
            var cache = Path.Combine(WorkRoot, "last.rgb");
            if (File.Exists(cache)) File.Delete(cache);
            File.Move(raw, cache);
            lastRgb = cache;
            // The player may update ReShade.ini during the render. Cache the
            // resulting settings timestamp so the next identical request hits.
            lastKey = CacheKey();
            await SendRgb(response, cache, inputBytes);
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

    private static async Task RunProcess(string executable, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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

    private static string PlayerPath()
    {
        // The installer writes a per-user path. Development builds can still
        // use the config beside this executable.
        var configs = new[]
        {
            Path.Combine(WorkRoot, "bridge-config.json"),
            Path.Combine(AppContext.BaseDirectory, "bridge-config.json")
        };
        foreach (var config in configs)
            if (File.Exists(config))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config));
                return document.RootElement.GetProperty("playerPath").GetString()!;
            }
        return Environment.GetEnvironmentVariable("DLSS_PHOTOSHOP_PLAYER") ?? "";
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(WorkRoot, "bridge.log"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine); }
        catch { }
    }
}
