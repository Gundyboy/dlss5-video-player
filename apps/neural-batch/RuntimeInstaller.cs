using System.Security.Cryptography;
using System.Text.Json;

namespace NeuralBatch;

internal static class RuntimeInstaller
{
    private sealed record LockedFile(string Name, string Hash);
    private static List<LockedFile> LockedFiles(string payload)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "runtime-lock.json")));
        return document.RootElement.GetProperty("entries").EnumerateArray().Select(entry =>
        {
            var name = entry.GetProperty("destination").GetString()!;
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid renderer package file name.");
            return new LockedFile(name, entry.GetProperty("sha256").GetString()!);
        }).ToList();
    }

    public static async Task ValidateAsync(string playerExe, string payload, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (!File.Exists(playerExe) || !string.Equals(Path.GetFileName(playerExe), "DLSSVideoPlayer.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose DLSSVideoPlayer.exe from the unpacked video player folder.");
        foreach (var name in new[] { "DLSSPhotoshopNeural.exe", "NeuralWorker.exe", "ReShade.ini", "ReShadePreset.ini", "runtime-lock.json" })
            if (!File.Exists(Path.Combine(payload, name))) throw new FileNotFoundException("The application package is incomplete. Reinstall DLSS Neural Batch.");
        foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            if (!File.Exists(Path.Combine(payload, "CRT", name))) throw new FileNotFoundException("The bundled C++ runtime is incomplete. Reinstall DLSS Neural Batch.");
        var source = Path.GetDirectoryName(Path.GetFullPath(playerExe))!;
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            if (!File.Exists(Path.Combine(source, name))) throw new FileNotFoundException($"The selected player folder is missing {name}.");
        var locked = LockedFiles(payload);
        for (var index = 0; index < locked.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"Verifying renderer files ({index + 1}/{locked.Count})…");
            var item = locked[index];
            var path = Path.Combine(source, "neural-runtime", item.Name);
            if (!File.Exists(path)) throw new FileNotFoundException($"The selected player is missing a required renderer file: {item.Name}");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
            if (!string.Equals(actual, item.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The selected player has a different renderer version ({item.Name}). Use the verified player bundle.");
        }
    }

    public static async Task InstallAsync(string playerExe, string payload, string destination, IProgress<string>? progress, CancellationToken cancellation)
    {
        await ValidateAsync(playerExe, payload, progress, cancellation);
        var target = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, "Renderer.stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, "Renderer.backup-" + Guid.NewGuid().ToString("N"));
        var source = Path.GetDirectoryName(Path.GetFullPath(playerExe))!;
        Directory.CreateDirectory(Path.Combine(stage, "neural-runtime"));
        try
        {
            progress?.Report("Preparing the local renderer…");
            var files = new List<(string Source, string Destination)>
            {
                (Path.Combine(payload, "DLSSPhotoshopNeural.exe"), Path.Combine(stage, "DLSSPhotoshopNeural.exe")),
                (Path.Combine(source, "ffmpeg.exe"), Path.Combine(stage, "ffmpeg.exe")),
                (Path.Combine(source, "ffprobe.exe"), Path.Combine(stage, "ffprobe.exe"))
            };
            foreach (var item in LockedFiles(payload)) files.Add((Path.Combine(source, "neural-runtime", item.Name), Path.Combine(stage, "neural-runtime", item.Name)));
            foreach (var name in new[] { "NeuralWorker.exe", "ReShade.ini", "ReShadePreset.ini" })
                files.Add((Path.Combine(payload, name), Path.Combine(stage, "neural-runtime", name)));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(payload, "CRT"), "*.dll"))
            {
                files.Add((file, Path.Combine(stage, Path.GetFileName(file))));
            }
            foreach (var name in new[] { "LICENSE", "THIRD_PARTY.md", "EXPERIMENTAL_RUNTIME_NOTICE.txt" })
                if (File.Exists(Path.Combine(source, name))) files.Add((Path.Combine(source, name), Path.Combine(stage, name)));
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(file.Source);
                await using var output = new FileStream(file.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(stage, target); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, target); throw; }
            // A failed backup cleanup does not invalidate the newly installed renderer.
            try { DeleteOwnedDirectory(backup, parent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { DeleteOwnedDirectory(stage, parent); }
    }

    internal static void DeleteOwnedDirectory(string directory, string parent)
    {
        var absolute = Path.GetFullPath(directory);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(absolute) != root) throw new IOException("Refusing to remove a folder outside the application work area.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
