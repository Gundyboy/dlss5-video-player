using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

// Two disk-backed frames keep both panorama views available for Mix changes.
// Render requests are exclusive, so these entries never need concurrent writes.
internal sealed class RenderCache
{
    private sealed record Entry(string Key, string Path);
    private readonly List<Entry> entries = new();
    private readonly string[] slots;

    internal RenderCache(string root)
    {
        slots = new[] { Path.Combine(root, "last.rgb"), Path.Combine(root, "previous.rgb") };
    }

    internal string? TryGet(string key, long expectedBytes)
    {
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index < 0) return null;
        var entry = entries[index];
        entries.RemoveAt(index);
        if (!File.Exists(entry.Path) || new FileInfo(entry.Path).Length != expectedBytes) return null;
        entries.Insert(0, entry);
        return entry.Path;
    }

    internal string Store(string key, string source, long expectedBytes)
    {
        if (!File.Exists(source) || new FileInfo(source).Length != expectedBytes)
            throw new IOException("The decoded neural image has the wrong dimensions.");
        var matching = entries.Find(entry => entry.Key == key);
        string? destination = matching?.Path;
        if (destination == null)
            foreach (var slot in slots)
                if (!entries.Exists(entry => entry.Path == slot)) { destination = slot; break; }
        destination ??= entries[entries.Count - 1].Path;
        File.Move(source, destination, true);
        entries.RemoveAll(entry => entry.Path == destination || entry.Key == key);
        entries.Insert(0, new Entry(key, destination));
        return destination;
    }

    internal static string SettingsFingerprint(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
}
