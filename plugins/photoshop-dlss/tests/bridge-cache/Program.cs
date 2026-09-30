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
    Console.WriteLine("Two-view cache reuse, eviction, frame integrity, and settings invalidation tests passed.");
}
finally
{
    var absoluteFixture = Path.GetFullPath(fixture);
    if (!absoluteFixture.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(absoluteFixture).StartsWith("dlss-pano-cache-test-", StringComparison.Ordinal))
        throw new Exception("Unsafe fixture cleanup path.");
    Directory.Delete(absoluteFixture, true);
}
