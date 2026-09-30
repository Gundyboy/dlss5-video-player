using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NeuralBatch;

internal abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

internal enum JobState { Queued, Running, Done, Failed, Stopped }

internal sealed class ImageJob(string inputPath) : NotifyObject
{
    public string InputPath { get; } = Path.GetFullPath(inputPath);
    public string FileName => Path.GetFileName(InputPath);
    public string Folder => Path.GetDirectoryName(InputPath)!;
    public string OutputPath { get; set; } = "";
    private JobState state;
    public JobState State { get => state; set { state = value; Notify(); } }
    private string status = "Waiting";
    public string Status { get => status; set { status = value; Notify(); } }
    private string size = "—";
    public string Size { get => size; set { size = value; Notify(); } }
}

internal sealed record BatchOptions(int Mix, bool Pano, string OutputFolder);
internal sealed record ImagePixels(int Width, int Height, byte[] Bgra, double DpiX = 96, double DpiY = 96);
internal sealed record JobProgress(string Phase, int Percent, string? Size = null);

internal sealed class Settings
{
    public int Mix { get; set; } = 100;
    public bool Pano { get; set; }
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DLSS Neural Mix");
    public string PlayerExe { get; set; } = "";
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSSNeuralBatch");
    public static string Renderer => Path.Combine(Root, "Renderer", "DLSSPhotoshopNeural.exe");
    private static string FilePath => Path.Combine(Root, "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class QueueRunner
{
    private TaskCompletionSource? resume;
    public bool IsPaused => resume != null;
    public void Pause() => resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Resume() { var signal = resume; resume = null; signal?.TrySetResult(); }

    public async Task RunAsync(IList<ImageJob> jobs, Func<ImageJob, CancellationToken, Task<string>> process,
        Action<ImageJob, Exception>? failed, CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!jobs.Any(item => item.State == JobState.Queued)) return;
                var pause = resume;
                if (pause != null) await pause.Task.WaitAsync(cancellation);
                var job = jobs.FirstOrDefault(item => item.State == JobState.Queued);
                if (job == null) return;
                job.State = JobState.Running;
                try
                {
                    job.OutputPath = await process(job, cancellation);
                    job.State = JobState.Done;
                    job.Status = "Done";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    job.State = JobState.Stopped; job.Status = "Stopped — ready to retry";
                    throw;
                }
                catch (Exception error)
                {
                    job.State = JobState.Failed; job.Status = "Failed: " + error.Message;
                    failed?.Invoke(job, error);
                }
            }
        }
        finally { Resume(); }
    }
}
