using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NeuralBatch;

internal static class HiddenProcess
{
    public static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        } };
        // The isolated worker inherits this private search path. Keep CRT DLLs
        // beside the runner: the model folder permits only its pinned modules.
        process.StartInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        process.StartInfo.Environment["PATH"] = Path.GetDirectoryName(executable) + Path.PathSeparator + inheritedPath;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Could not start the local renderer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var stop = timeout.Token.Register(() =>
        { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // Kill only this application's helper tree and wait for pipe closure.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (!cancellation.IsCancellationRequested) throw new TimeoutException("The renderer exceeded ten minutes for this image.");
            throw;
        }
        var details = (await stdout + await stderr).Trim();
        if (details.Length > 4096) details = details[..2048] + "\n…\n" + details[^2048..];
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrEmpty(details) ? "The local renderer failed." : details);
    }
}

internal sealed class Renderer
{
    private readonly string runner;
    private readonly RenderCache cache;
    public Renderer(string runner, string root)
    {
        this.runner = Path.GetFullPath(runner);
        Directory.CreateDirectory(root); cache = new(root);
    }

    internal async Task<byte[]> RenderAsync(byte[] rgb, int width, int height, string job, CancellationToken cancellation)
    {
        var directory = Path.GetDirectoryName(runner)!;
        var worker = Path.Combine(directory, "neural-runtime", "NeuralWorker.exe");
        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        if (!File.Exists(runner) || !File.Exists(worker) || !File.Exists(ffmpeg))
            throw new FileNotFoundException("The renderer is unavailable. Choose the video player executable again.");
        var settings = Path.Combine(directory, "neural-runtime", "ReShade.ini");
        var digest = Convert.ToHexString(SHA256.HashData(rgb));
        string Key() => digest + ":" + width + "x" + height + ":" + runner + ":" + File.GetLastWriteTimeUtc(runner).Ticks + ":" +
            File.GetLastWriteTimeUtc(worker).Ticks + ":" + RenderCache.SettingsFingerprint(settings);
        var cached = cache.TryGet(Key(), rgb.LongLength);
        if (cached != null) return await File.ReadAllBytesAsync(cached, cancellation);
        var input = Path.Combine(job, "input.bmp");
        var video = Path.Combine(job, "output.mkv");
        var raw = Path.Combine(job, "output.rgb");
        if (File.Exists(video)) File.Delete(video);
        await WriteBmpAsync(input, rgb, width, height, cancellation);
        await HiddenProcess.RunAsync(runner, new[] { "--input", input, "--output", video, "--width", width.ToString(), "--height", height.ToString() }, cancellation);
        if (!File.Exists(video)) throw new IOException("The renderer did not return a verified image.");
        await HiddenProcess.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw }, cancellation);
        var stored = cache.Store(Key(), raw, rgb.LongLength);
        return await File.ReadAllBytesAsync(stored, cancellation);
    }

    internal static async Task WriteBmpAsync(string path, byte[] rgb, int width, int height, CancellationToken cancellation)
    {
        var rowBytes = checked(width * 3);
        var stride = (rowBytes + 3) & ~3;
        using var destination = File.Create(path);
        using var writer = new BinaryWriter(destination, Encoding.ASCII, true);
        writer.Write((ushort)0x4D42); writer.Write(54 + stride * height); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(stride * height); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            { var s = (y * width + x) * 3; var d = x * 3; row[d] = rgb[s + 2]; row[d + 1] = rgb[s + 1]; row[d + 2] = rgb[s]; }
            await destination.WriteAsync(row, cancellation);
        }
    }
}

internal sealed class ImageProcessor(string runner, string workRoot)
{
    private readonly string root = Path.GetFullPath(workRoot);
    private readonly Renderer renderer = new(runner, Path.Combine(workRoot, "cache"));

    public async Task<string> ProcessAsync(string input, BatchOptions options, IProgress<JobProgress>? progress, CancellationToken cancellation)
    {
        if (options.Mix < 0 || options.Mix > 200) throw new ArgumentOutOfRangeException(nameof(options), "Mix must be 0–200%.");
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Reading image", 5));
        var source = await Task.Run(() => ImageFiles.Read(input), cancellation);
        progress?.Report(new("Image ready", 10, $"{source.Width} × {source.Height}"));
        if (options.Mix == 0)
        {
            progress?.Report(new("Saving original pixels", 90));
            return await Task.Run(() => ImageFiles.SaveResult(input, options.OutputFolder, source, options, cancellation), cancellation);
        }
        var job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var rgb = await Task.Run(() => PixelMath.ToRgb(source), cancellation);
            progress?.Report(new(options.Pano ? "Pano 1/2 — original view" : "Neural rendering", 20));
            var neural = await renderer.RenderAsync(rgb, source.Width, source.Height, job, cancellation);
            if (options.Pano)
            {
                var offset = source.Width / 2;
                await Task.Run(() => PixelMath.Offset(rgb, source.Width, source.Height, offset), cancellation);
                progress?.Report(new("Pano 2/2 — seam view", 55));
                var shifted = await renderer.RenderAsync(rgb, source.Width, source.Height, job, cancellation);
                progress?.Report(new("Blending panorama seam", 80));
                await Task.Run(() => PixelMath.BlendSeam(neural, shifted, source.Width, source.Height, offset), cancellation);
            }
            progress?.Report(new("Applying Mix", 85));
            var output = await Task.Run(() => PixelMath.Compose(source, neural, options.Mix), cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new("Saving PNG", 95));
            return await Task.Run(() => ImageFiles.SaveResult(input, options.OutputFolder, source with { Bgra = output }, options, cancellation), cancellation);
        }
        finally
        {
            // A cancelled child may briefly retain a file handle while exiting.
            // Cleanup must not turn a successful render or cancellation into a failure.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { RuntimeInstaller.DeleteOwnedDirectory(job, root); break; }
                catch (IOException) { if (attempt < 4) await Task.Delay(200); }
                catch (UnauthorizedAccessException) { if (attempt < 4) await Task.Delay(200); }
            }
        }
    }
}
