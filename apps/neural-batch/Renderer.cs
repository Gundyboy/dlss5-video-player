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
        Directory.CreateDirectory(root); cache = new(root, 4);
    }

    internal async Task<byte[]> RenderAsync(byte[] rgb, int width, int height, int outputWidth, int outputHeight,
        bool neural, string job, CancellationToken cancellation)
    {
        var directory = Path.GetDirectoryName(runner)!;
        var worker = Path.Combine(directory, "neural-runtime", "NeuralWorker.exe");
        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        if (!File.Exists(runner) || !File.Exists(worker) || !File.Exists(ffmpeg))
            throw new FileNotFoundException("The renderer is unavailable. Choose the video player executable again.");
        var settings = Path.Combine(directory, "neural-runtime", "ReShade.ini");
        var digest = Convert.ToHexString(SHA256.HashData(rgb));
        var outputBytes = checked((long)outputWidth * outputHeight * 3);
        string Key() => "batch-v2:" + digest + ":" + width + "x" + height + ":" + outputWidth + "x" + outputHeight + ":neural=" + neural + ":" + runner + ":" + File.GetLastWriteTimeUtc(runner).Ticks + ":" +
            File.GetLastWriteTimeUtc(worker).Ticks + ":" + RenderCache.SettingsFingerprint(settings);
        var cached = cache.TryGet(Key(), outputBytes);
        if (cached != null) return await File.ReadAllBytesAsync(cached, cancellation);
        var input = Path.Combine(job, "input.bmp");
        var video = Path.Combine(job, "output.mkv");
        var raw = Path.Combine(job, "output.rgb");
        if (File.Exists(video)) File.Delete(video);
        await WriteBmpAsync(input, rgb, width, height, cancellation);
        var arguments = new List<string> { "--input", input, "--output", video, "--width", width.ToString(), "--height", height.ToString() };
        if (outputWidth != width || outputHeight != height)
            arguments.AddRange(new[] { "--output-width", outputWidth.ToString(), "--output-height", outputHeight.ToString(), "--neural", neural ? "on" : "off" });
        await HiddenProcess.RunAsync(runner, arguments, cancellation);
        if (!File.Exists(video)) throw new IOException("The renderer did not return a verified image.");
        await HiddenProcess.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw }, cancellation);
        var stored = cache.Store(Key(), raw, outputBytes);
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
        var (outputWidth, outputHeight) = PixelMath.OutputSize(source.Width, source.Height, options.Scale);
        progress?.Report(new("Image ready", 10, options.Scale == 1 ? $"{source.Width} × {source.Height}" :
            $"{source.Width} × {source.Height} → {outputWidth} × {outputHeight}"));
        if (options.Mix == 0 && options.Scale == 1)
        {
            progress?.Report(new("Saving original pixels", 90));
            return await Task.Run(() => ImageFiles.SaveResult(input, options.OutputFolder, source, options, cancellation), cancellation);
        }
        var job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var rgb = await Task.Run(() => PixelMath.ToRgb(source), cancellation);
            async Task<byte[]> RenderView(bool neuralPass)
            {
                var mode = neuralPass ? (options.Scale == 2 ? "DLSS 2× + neural" : "Neural rendering") : "DLSS 2× upscaling";
                progress?.Report(new(mode + (options.Pano ? " · Pano 1/2 — original view" : ""), 20));
                var frame = await renderer.RenderAsync(rgb, source.Width, source.Height, outputWidth, outputHeight, neuralPass, job, cancellation);
                if (options.Pano)
                {
                    var offset = source.Width / 2;
                    await Task.Run(() => PixelMath.Offset(rgb, source.Width, source.Height, offset), cancellation);
                    progress?.Report(new(mode + " · Pano 2/2 — seam view", 55));
                    var shifted = await renderer.RenderAsync(rgb, source.Width, source.Height, outputWidth, outputHeight, neuralPass, job, cancellation);
                    progress?.Report(new("Blending panorama seam", 80));
                    // Integer 2× scaling keeps the inverse shift exact for odd widths too.
                    await Task.Run(() => PixelMath.BlendSeam(frame, shifted, outputWidth, outputHeight, offset * options.Scale), cancellation);
                    await Task.Run(() => PixelMath.Offset(rgb, source.Width, source.Height, source.Width - offset), cancellation);
                }
                return frame;
            }
            var reference = options.Scale == 2 ? await Task.Run(() => PixelMath.UpscaledAlpha(source, options.Pano), cancellation) : source;
            if (options.Scale == 2 && options.Mix is not (0 or 100))
            {
                var baseline = await RenderView(false);
                await Task.Run(() => PixelMath.SetRgb(reference, baseline), cancellation);
            }
            var neural = await RenderView(options.Mix > 0);
            progress?.Report(new("Applying Mix", 85));
            var output = await Task.Run(() => PixelMath.Compose(reference, neural, options.Mix == 0 ? 100 : options.Mix), cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new("Saving PNG", 95));
            return await Task.Run(() => ImageFiles.SaveResult(input, options.OutputFolder, reference with { Bgra = output }, options, cancellation), cancellation);
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
