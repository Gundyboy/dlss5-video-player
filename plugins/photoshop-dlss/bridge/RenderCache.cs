using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

// Photoshop defaults to two views; Batch can retain four for SR/NR panoramas.
// Render requests are exclusive, so these entries never need concurrent writes.
internal sealed class RenderCache
{
    private sealed record Entry(string Key, string Path);
    private readonly List<Entry> entries = new();
    private readonly string[] slots;

    internal RenderCache(string root, int capacity = 2)
    {
        if (capacity < 2 || capacity > 4) throw new ArgumentOutOfRangeException(nameof(capacity));
        slots = new string[capacity];
        for (var index = 0; index < capacity; index++)
            slots[index] = Path.Combine(root, index == 0 ? "last.rgb" : index == 1 ? "previous.rgb" : "view-" + index + ".rgb");
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

    internal (int Files, long Bytes) Clear()
    {
        // Invalidate keys first, including after a partial deletion failure.
        // Inspect every owned slot so files left by an earlier bridge also go.
        entries.Clear();
        var files = 0;
        long bytes = 0;
        foreach (var slot in slots)
        {
            if (!File.Exists(slot)) continue;
            var length = new FileInfo(slot).Length;
            File.Delete(slot);
            files++;
            bytes += length;
        }
        return (files, bytes);
    }
}
