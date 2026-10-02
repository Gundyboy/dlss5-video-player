using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NeuralBatch;

internal static class Tests
{
    private static string fixture = "";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Fails(Func<Task> action)
    { try { await action(); } catch { return; } throw new Exception("Expected operation to fail."); }

    [STAThread]
    public static int Main(string[] args)
    {
        var dist = Path.GetFullPath(args.Length > 0 ? args[0] : "apps/neural-batch/dist");
        Directory.CreateDirectory(dist);
        fixture = Path.Combine(dist, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            PixelTests(); FileTests(); CacheTests(); RenderUi(dist);
            RunAsync(args.Contains("--gpu"), args.Contains("--packaged-runtime") ? Path.Combine(dist, "portable", "RuntimeFiles") : null).GetAwaiter().GetResult();
            Console.WriteLine("Batch tests passed: pixel math, image IO/orientation/alpha, collision safety, queue pause/stop/errors, runtime install, hidden-process cancellation, and compact UI layout.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { RuntimeInstaller.DeleteOwnedDirectory(fixture, dist); }
    }

    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var p = (y * width + x) * 4; pixels[p] = (byte)(40 + y % 150); pixels[p + 1] = (byte)(60 + x % 100); pixels[p + 2] = (byte)(100 + (x + y) % 100); pixels[p + 3] = 77; }
        return pixels;
    }
    private static void PixelTests()
    {
        foreach (var width in new[] { 64, 65, 8192 })
        {
            var source = PixelMath.ToRgb(new(width, 2, Pattern(width, 2)));
            var shifted = source.ToArray(); var offset = width / 2;
            PixelMath.Offset(shifted, width, 2, offset);
            for (var y = 0; y < 2; y++) for (var x = 0; x < width; x++) for (var c = 0; c < 3; c++)
                Check(shifted[(y * width + x) * 3 + c] == source[(y * width + (x + offset) % width) * 3 + c], "Offset changed orientation or crossed rows.");
            var altered = shifted.ToArray(); var direct = source.ToArray();
            for (var y = 0; y < 2; y++) foreach (var x in new[] { 0, width - 1 })
                for (var c = 0; c < 3; c++) { altered[(y * width + x) * 3 + c] = 0; direct[(y * width + x) * 3 + c] = 255; }
            PixelMath.BlendSeam(direct, altered, width, 2, offset);
            Check(direct.SequenceEqual(source), "Pano introduced artificial render borders.");
            PixelMath.Offset(shifted, width, 2, width - offset);
            Check(shifted.SequenceEqual(source), "Offset must reverse without resampling.");
        }
        var enhanced = PixelMath.Compose(new(1, 1, new byte[] { 100, 100, 100, 77 }), new byte[] { 150, 150, 150 }, 200);
        Check(enhanced.SequenceEqual(new byte[] { 205, 205, 205, 77 }), "200% Mix must match the Photoshop plugin.");
        foreach (var pair in new[] { (50, 125), (100, 150) })
            Check(PixelMath.Compose(new(1, 1, new byte[] { 100, 100, 100, 77 }), new byte[] { 150, 150, 150 }, pair.Item1)[0] == pair.Item2, "Mix is incorrect.");
        CheckThrows(() => PixelMath.ValidateDimensions(8193, 256));
        CheckThrows(() => PixelMath.ValidateDimensions(8192, 8192));
        PixelMath.ValidateDimensions(8192, 4096);
        Check(PixelMath.OutputSize(129, 65, 2) == (258, 130), "Odd source upscale must preserve exact doubled dimensions.");
        CheckThrows(() => PixelMath.OutputSize(8192, 4096, 2));
        CheckThrows(() => PixelMath.OutputSize(128, 64, 3));
        var alpha = new ImagePixels(64, 64, new byte[64 * 64 * 4]);
        for (var y = 0; y < 64; y++) { alpha.Bgra[(y * 64) * 4 + 3] = 0; alpha.Bgra[(y * 64 + 1) * 4 + 3] = 255; alpha.Bgra[(y * 64 + 63) * 4 + 3] = 200; }
        var resized = PixelMath.UpscaledAlpha(alpha, false);
        Check(resized.Bgra[3] == 0 && resized.Bgra[7] == 64 && resized.Bgra[11] == 191 && resized.Bgra[15] == 191, "Alpha interpolation or pixel-center alignment changed.");
        var wrapped = PixelMath.UpscaledAlpha(alpha, true);
        Check(wrapped.Bgra[3] == 50 && wrapped.Bgra[(127 * 4) + 3] == 150, "Pano alpha must wrap at the horizontal seam.");
    }
    private static void CheckThrows(Action action) { try { action(); } catch { return; } throw new Exception("Expected validation failure."); }

    private static void FileTests()
    {
        var input = Path.Combine(fixture, "alpha.png");
        var source = new ImagePixels(128, 64, Pattern(128, 64), 144, 144);
        ImageFiles.WritePng(input, source);
        var read = ImageFiles.Read(input);
        Check(read.Bgra.SequenceEqual(source.Bgra), "PNG must preserve source color and alpha.");
        var originalHash = SHA256.HashData(File.ReadAllBytes(input));
        var options = new BatchOptions(100, true, fixture);
        var first = ImageFiles.SaveResult(input, fixture, read, options, CancellationToken.None);
        var second = ImageFiles.SaveResult(input, fixture, read, options, CancellationToken.None);
        Check(first != second && File.Exists(first), "Output collision must not overwrite an earlier result.");
        Check(SHA256.HashData(File.ReadAllBytes(input)).SequenceEqual(originalHash), "Original image was modified.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        CheckThrows(() => ImageFiles.SaveResult(input, fixture, read, options, cancelled.Token));
        Check(!Directory.EnumerateFiles(fixture, ".dlss-*.tmp").Any(), "Cancelled output must leave no temporary files.");
        var bitmap = BitmapSource.Create(128, 64, 96, 96, PixelFormats.Bgra32, null, source.Bgra, 128 * 4);
        var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        var jpeg = Path.Combine(fixture, "oriented.jpg"); using (var stream = File.Create(jpeg)) encoder.Save(stream);
        var oriented = ImageFiles.Read(jpeg);
        Check(oriented.Width == 64 && oriented.Height == 128, "JPEG orientation must be applied once.");
        foreach (var orientation in Enumerable.Range(2, 7))
        {
            var result = ImageFiles.Orient(source, orientation);
            var inverse = orientation == 6 ? 8 : orientation == 8 ? 6 : orientation;
            Check(ImageFiles.Orient(result, inverse).Bgra.SequenceEqual(source.Bgra), "EXIF orientation is not reversible.");
        }
    }

    private static void RenderUi(string dist)
    {
        var application = new App(); application.InitializeComponent();
        foreach (var size in new[] { (1120, 880, "ui-default.png"), (820, 760, "ui-compact.png") })
        {
            var window = new MainWindow();
            window.SelectedUpscale = window.UpscaleChoices[1];
            window.Jobs.Add(new ImageJob(Path.Combine(fixture, "Tower Hero.jpg")) { Size = "4096 × 2048", Status = "Done", State = JobState.Done });
            window.Jobs.Add(new ImageJob(Path.Combine(fixture, "360 Panorama.png")) { Size = "8192 × 4096", Status = "Waiting" });
            var root = (Grid)window.Content;
            root.Background = new SolidColorBrush(Color.FromRgb(23, 27, 36));
            root.Measure(new Size(size.Item1, size.Item2)); root.Arrange(new Rect(0, 0, size.Item1, size.Item2)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var footer = root.Children.OfType<DockPanel>().Single(child => Grid.GetRow(child) == 7);
            var footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(0, 0, footer.ActualWidth, footer.ActualHeight));
            Check(footerBounds.Bottom <= root.ActualHeight + 1, "Compact window clips its footer.");
            var upscaler = (ComboBox)window.FindName("UpscalerControl");
            Check(upscaler.SelectedItem is UpscaleChoice { Scale: 2 } && upscaler.ActualWidth > 200, "Upscaler binding or layout failed.");
            var target = new RenderTargetBitmap(size.Item1, size.Item2, 96, 96, PixelFormats.Pbgra32); target.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = File.Create(Path.Combine(dist, size.Item3)); encoder.Save(stream);
        }
    }

    private static void CacheTests()
    {
        var root = Path.Combine(fixture, "cache-check"); Directory.CreateDirectory(root);
        var cache = new RenderCache(root, 4);
        for (var view = 0; view < 4; view++) { var path = Path.Combine(root, "incoming.rgb"); File.WriteAllBytes(path, new byte[] { (byte)view, 1, 2 }); cache.Store("view" + view, path, 3); }
        var saved = cache.TryGet("view0", 3); Check(saved != null && File.ReadAllBytes(saved)[0] == 0, "Mixed Pano must retain all four GPU views.");
        var extra = Path.Combine(root, "incoming.rgb"); File.WriteAllBytes(extra, new byte[] { 4, 1, 2 }); cache.Store("view4", extra, 3);
        Check(cache.TryGet("view1", 3) == null && cache.TryGet("view0", 3) != null, "Cache must evict the least recently used frame.");
        Check(cache.TryGet("view4", 4) == null && Directory.GetFiles(root).Length == 4, "Cache must reject incorrect dimensions and stay bounded.");
    }

    private static async Task RunAsync(bool gpu, string? payload)
    {
        var jobs = new List<ImageJob> { new("first.png"), new("bad.png"), new("third.png") };
        var order = new List<string>();
        await new QueueRunner().RunAsync(jobs, (job, _) =>
        { order.Add(job.FileName); if (job.FileName == "bad.png") throw new IOException("Bad image"); return Task.FromResult(job.FileName + ".out"); }, null, CancellationToken.None);
        Check(order.SequenceEqual(new[] { "first.png", "bad.png", "third.png" }) && jobs[1].State == JobState.Failed && jobs[2].State == JobState.Done, "One failed image must not stop or reorder the queue.");

        var queue = new QueueRunner(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs = new() { new("one.png"), new("two.png") }; var processed = 0;
        var running = queue.RunAsync(jobs, (job, _) => { if (++processed == 1) { queue.Pause(); entered.SetResult(); } return Task.FromResult("out"); }, null, CancellationToken.None);
        await entered.Task; await Task.Delay(100); Check(processed == 1, "Pause must stop before the next image.");
        jobs.Add(new("added.png")); queue.Resume(); await running; Check(processed == 3, "Added images must join the running queue.");
        jobs = new() { new("last.png") };
        await queue.RunAsync(jobs, (_, _) => { queue.Pause(); return Task.FromResult("out"); }, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Check(!queue.IsPaused && jobs[0].State == JobState.Done, "Pausing the last image must finish an empty queue.");
        using var stop = new CancellationTokenSource(100);
        jobs = new() { new("current.png"), new("waiting.png") };
        await Fails(() => queue.RunAsync(jobs, async (_, token) => { await Task.Delay(10000, token); return "out"; }, null, stop.Token));
        Check(jobs[0].State == JobState.Stopped && jobs[1].State == JobState.Queued, "Stop must retain waiting images.");

        var zero = new ImageProcessor(Path.Combine(fixture, "missing-runner.exe"), Path.Combine(fixture, "zero-work"));
        await zero.ProcessAsync(Path.Combine(fixture, "alpha.png"), new(0, true, Path.Combine(fixture, "zero-out")), null, CancellationToken.None);
        using var cancelProcess = new CancellationTokenSource(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Fails(() => HiddenProcess.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"), new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" }, cancelProcess.Token));
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Stop must terminate the owned hidden helper promptly.");
        await RuntimeTests();
        if (gpu) await GpuTest(payload);
    }

    private static async Task RuntimeTests()
    {
        var source = Path.Combine(fixture, "player"); var runtime = Path.Combine(source, "neural-runtime"); var payload = Path.Combine(fixture, "payload");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(payload);
        foreach (var name in new[] { "DLSSVideoPlayer.exe", "ffmpeg.exe", "ffprobe.exe" }) File.WriteAllText(Path.Combine(source, name), "fixture");
        foreach (var name in new[] { "DLSSPhotoshopNeural.exe", "NeuralWorker.exe", "ReShade.ini", "ReShadePreset.ini" }) File.WriteAllText(Path.Combine(payload, name), "fixture");
        Directory.CreateDirectory(Path.Combine(payload, "CRT"));
        foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" }) File.WriteAllText(Path.Combine(payload, "CRT", name), "fixture");
        var binary = Path.Combine(runtime, "model.dll"); File.WriteAllText(binary, "verified");
        File.WriteAllText(Path.Combine(payload, "runtime-lock.json"), JsonSerializer.Serialize(new { entries = new[] { new { destination = "model.dll", sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))) } } }));
        var destination = Path.Combine(fixture, "installed"); var player = Path.Combine(source, "DLSSVideoPlayer.exe");
        await RuntimeInstaller.InstallAsync(player, payload, destination, null, CancellationToken.None);
        Check(File.Exists(Path.Combine(destination, "neural-runtime", "model.dll")), "Runtime installation must copy the verified model.");
        Check(File.Exists(Path.Combine(destination, "vcruntime140.dll")) && !File.Exists(Path.Combine(destination, "neural-runtime", "vcruntime140.dll")), "CRT deployment must preserve the model folder's pinned module policy.");
        File.WriteAllText(binary, "wrong version");
        await Fails(() => RuntimeInstaller.InstallAsync(player, payload, destination, null, CancellationToken.None));
        Check(File.ReadAllText(Path.Combine(destination, "neural-runtime", "model.dll")) == "verified", "A failed upgrade must retain the old renderer.");
        Check(!Directory.EnumerateDirectories(fixture, "Renderer.stage-*").Any(), "Runtime staging folder was not cleaned.");
    }

    private static async Task GpuTest(string? payload)
    {
        var runner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSSNeuralMix/Renderer/DLSSPhotoshopNeural.exe");
        if (payload != null)
        {
            var destination = Path.Combine(fixture, "private-renderer");
            await RuntimeInstaller.InstallAsync(@"C:\Users\William\Desktop\DLSSVideoPlayer-v0.26.0-win64\DLSSVideoPlayer.exe", payload, destination, null, CancellationToken.None);
            runner = Path.Combine(destination, "DLSSPhotoshopNeural.exe");
            Console.WriteLine("Testing the packaged private renderer and bundled C++ runtime.");
        }
        var processor = new ImageProcessor(runner, Path.Combine(fixture, "gpu-work"));
        var first = Path.Combine(fixture, "gpu-a.png"); var second = Path.Combine(fixture, "gpu-b.png"); var bad = Path.Combine(fixture, "bad.png");
        ImageFiles.WritePng(first, new(256, 128, Pattern(256, 128)));
        var pixels = Pattern(256, 128); pixels[40] = 240;
        ImageFiles.WritePng(second, new(256, 128, pixels)); File.WriteAllText(bad, "not an image");
        var jobs = new List<ImageJob> { new(first), new(bad), new(second) };
        var options = new BatchOptions(100, true, Path.Combine(fixture, "gpu-out"));
        await new QueueRunner().RunAsync(jobs, async (job, token) =>
        { Console.WriteLine("GPU queue: " + job.FileName); return await processor.ProcessAsync(job.InputPath, options, null, token); }, (_, error) => Console.WriteLine("Expected queue error: " + error.Message), CancellationToken.None);
        Check(jobs[0].State == JobState.Done && jobs[1].State == JobState.Failed && jobs[2].State == JobState.Done, "Real GPU queue must continue after a bad image.");
        foreach (var job in jobs.Where(job => job.State == JobState.Done))
        {
            var output = ImageFiles.Read(job.OutputPath); Check(output.Width == 256 && output.Height == 128, "GPU output dimensions changed.");
            Check(output.Bgra.Where((_, index) => index % 4 == 3).All(alpha => alpha == 77), "GPU output lost source alpha.");
        }
        Check(!Directory.EnumerateDirectories(Path.Combine(fixture, "gpu-work"), "job-*").Any(), "Completed GPU jobs must leave no work folders.");
        Console.WriteLine("Real GPU batch passed: two Pano images, one bad image skipped, correct output dimensions and alpha.");
        if (payload == null) return; // The older installed PS runner has no upscaler CLI.
        var odd = Path.Combine(fixture, "odd-pano.png");
        var oddPixels = Pattern(129, 65);
        for (var index = 3; index < oddPixels.Length; index += 4) oddPixels[index] = (byte)((index / 4) % 256);
        ImageFiles.WritePng(odd, new(129, 65, oddPixels));
        var upscaledOptions = new BatchOptions(0, true, options.OutputFolder, 2);
        Console.WriteLine("GPU upscale: 0% Mix, odd-width Pano, variable alpha.");
        var sr = ImageFiles.Read(await processor.ProcessAsync(odd, upscaledOptions, null, CancellationToken.None));
        Check(sr.Width == 258 && sr.Height == 130, "2x SR-only did not produce the requested dimensions.");
        var expectedAlpha = PixelMath.UpscaledAlpha(new(129, 65, oddPixels), true);
        for (var index = 3; index < sr.Bgra.Length; index += 4) Check(sr.Bgra[index] == expectedAlpha.Bgra[index], "Upscaler changed alpha or wrap interpolation.");
        Console.WriteLine("GPU upscale: 100% Mix, SR plus neural Pano.");
        var nr = ImageFiles.Read(await processor.ProcessAsync(odd, upscaledOptions with { Mix = 100 }, null, CancellationToken.None));
        Check(nr.Width == 258 && nr.Height == 130, "2x SR plus neural changed dimensions.");
        string CacheSnapshot() => string.Join(";", Directory.GetFiles(Path.Combine(fixture, "gpu-work", "cache")).Order().Select(path => Path.GetFileName(path) + File.GetLastWriteTimeUtc(path).Ticks + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        var cacheBefore = CacheSnapshot();
        Console.WriteLine("GPU upscale: 50% and 200% Mix must reuse cached SR/NR panorama views.");
        var half = ImageFiles.Read(await processor.ProcessAsync(odd, upscaledOptions with { Mix = 50 }, null, CancellationToken.None));
        for (var index = 0; index < half.Bgra.Length; index++)
            Check(half.Bgra[index] == (index % 4 == 3 ? sr.Bgra[index] : (sr.Bgra[index] + nr.Bgra[index] + 1) / 2), "Partial Mix used a different reference from DLSS SR-only.");
        var enhanced = ImageFiles.Read(await processor.ProcessAsync(odd, upscaledOptions with { Mix = 200 }, null, CancellationToken.None));
        Check(enhanced.Bgra.SequenceEqual(PixelMath.Compose(sr, PixelMath.ToRgb(nr), 200)), "200% upscale Mix did not use the SR-only reference.");
        Check(CacheSnapshot() == cacheBefore, "Mix changes must reuse all four cached views without rerendering.");
        var tooWide = Path.Combine(fixture, "too-wide.png"); ImageFiles.WritePng(tooWide, new(4097, 64, Pattern(4097, 64)));
        var remaining = new List<ImageJob> { new(tooWide), new(odd) };
        await new QueueRunner().RunAsync(remaining, (job, token) => processor.ProcessAsync(job.InputPath, upscaledOptions, null, token), (_, error) => Console.WriteLine("Expected oversized output: " + error.Message), CancellationToken.None);
        Check(remaining[0].State == JobState.Failed && remaining[1].State == JobState.Done, "Oversized upscale output must fail only that image and continue.");
        Check(!Directory.EnumerateDirectories(Path.Combine(fixture, "gpu-work"), "job-*").Any(), "Upscale jobs left temporary folders.");
        Console.WriteLine("Real 2x DLSS upscaler passed: SR-only, combined neural, 50/200% Mix, odd Pano, alpha, cache reuse and queue limits.");
    }
}
