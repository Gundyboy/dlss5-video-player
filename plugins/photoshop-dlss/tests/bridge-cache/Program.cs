using System;
using System.IO;

var tempRoot = Path.GetFullPath(Path.GetTempPath());
var fixture = Path.Combine(tempRoot, "dlss-pano-cache-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var frames = new RenderCache(fixture);
    string Frame(string key, byte value)
    {
        var raw = Path.Combine(fixture, key + ".raw");
        File.WriteAllBytes(raw, new byte[] { value, value, value });
        return frames.Store(key, raw, 3);
    }
    void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    var first = Frame("original", 10);
    var second = Frame("offset", 20);
    Check(first != second, "The two panorama views must use separate disk frames.");
    for (var i = 0; i < 4; i++)
    {
        Check(File.ReadAllBytes(frames.TryGet("original", 3)!)[0] == 10, "Original view must survive repeated Mix changes.");
        Check(File.ReadAllBytes(frames.TryGet("offset", 3)!)[0] == 20, "Offset view must survive repeated Mix changes.");
    }
    Check(frames.TryGet("other-settings", 3) == null, "Settings mismatch must never reuse a frame.");
    var third = Frame("new-layer", 30);
    Check(frames.TryGet("original", 3) == null, "The least recently used view must be evicted.");
    Check(frames.TryGet("offset", 3) != null && frames.TryGet("new-layer", 3) == third, "New entries must preserve the other recent view.");
    File.WriteAllBytes(second, new byte[] { 0 });
    Check(frames.TryGet("offset", 3) == null, "A truncated cached frame must be rejected.");
    var restored = Frame("offset", 40);
    File.Delete(restored);
    Check(frames.TryGet("offset", 3) == null, "A missing cached frame must be rejected.");
    Check(Directory.GetFiles(fixture, "*.rgb").Length <= 2, "Disk cache must remain bounded to two frames.");

    var settings = Path.Combine(fixture, "settings.ini");
    File.WriteAllText(settings, "NRPreUpscale=0\n");
    var fingerprint = RenderCache.SettingsFingerprint(settings);
    File.SetLastWriteTimeUtc(settings, DateTime.UtcNow.AddMinutes(1));
    Check(RenderCache.SettingsFingerprint(settings) == fingerprint, "Touching unchanged settings must preserve both cached views.");
    File.WriteAllText(settings, "NRPreUpscale=1\n");
    Check(RenderCache.SettingsFingerprint(settings) != fingerprint, "Changed rendering settings must invalidate the cached views.");

    first = Frame("original", 10);
    second = Frame("offset", 20);
    var cleared = frames.Clear();
    Check(cleared.Files == 2 && cleared.Bytes == 6, "Clearing must report both frames and their size.");
    Check(!File.Exists(first) && !File.Exists(second), "Clearing must delete both owned disk slots.");
    Check(frames.TryGet("original", 3) == null && frames.TryGet("offset", 3) == null, "Clearing must invalidate the in-memory keys.");
    Check(File.Exists(settings), "Clearing must retain unrelated files in the work directory.");
    Check(frames.Clear() == (0, 0L), "Clearing an empty cache must be safe and idempotent.");
    Frame("original", 30);
    Frame("offset", 40);
    var restarted = new RenderCache(fixture);
    Check(restarted.Clear() == (2, 6L), "Clearing must delete frames from a previous session with no in-memory keys.");
    frames.Clear();
    first = Frame("original", 50);
    using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { frames.Clear(); throw new Exception("A locked cache file must report failure."); }
        catch (IOException) { }
        Check(frames.TryGet("original", 3) == null, "A failed clear must still invalidate cached keys.");
    }
    Check(frames.Clear() == (1, 3L), "A failed deletion must be retryable once the file unlocks.");
    first = Frame("after-clear", 60);
    Check(frames.TryGet("after-clear", 3) == first, "Rendering must be able to populate the cache after clearing.");
    Console.WriteLine("Two-view cache reuse, eviction, integrity, settings invalidation, and safe cache clearing passed.");
}
finally
{
    var absoluteFixture = Path.GetFullPath(fixture);
    if (!absoluteFixture.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(absoluteFixture).StartsWith("dlss-pano-cache-test-", StringComparison.Ordinal))
        throw new Exception("Unsafe fixture cleanup path.");
    Directory.Delete(absoluteFixture, true);
}
