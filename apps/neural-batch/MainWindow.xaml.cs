using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NeuralBatch;

internal partial class MainWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ImageJob> Jobs { get; } = new();
    private readonly Settings settings = Settings.Load();
    private readonly QueueRunner queue = new();
    private readonly List<string> messages = new();
    private readonly DispatcherTimer elapsed = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? cancellation;
    private bool configuring;
    private string rendererStatus = "";
    private string current = "Ready. Add images to begin.";
    private string phase = "";
    private DateTime started;
    private ImageProcessor? processor;
    public int Mix { get => settings.Mix; set { settings.Mix = Math.Clamp(value, 0, 200); Changed(nameof(Mix)); Refresh(); } }
    public bool Pano { get => settings.Pano; set { settings.Pano = value; Changed(nameof(Pano)); } }
    public string OutputFolder { get => settings.OutputFolder; set { settings.OutputFolder = value; Changed(nameof(OutputFolder)); Refresh(); } }
    public bool IsRunning => cancellation != null;
    public bool CanConfigure => !IsRunning && !configuring;
    public bool CanStart => CanConfigure && Jobs.Any(job => job.State == JobState.Queued) && (Mix == 0 || File.Exists(Settings.Renderer)) && !string.IsNullOrWhiteSpace(OutputFolder);
    public string Counts => $"{Jobs.Count(job => job.State == JobState.Queued)} waiting · {Jobs.Count(job => job.State == JobState.Done)} done · {Jobs.Count(job => job.State == JobState.Failed)} failed";
    public string RendererStatus { get => rendererStatus; private set { rendererStatus = value; Changed(nameof(RendererStatus)); } }
    public string CurrentStatus { get => current; private set { current = value; Changed(nameof(CurrentStatus)); } }
    public string PauseText => queue.IsPaused ? "Resume" : "Pause";
    public string LogText => string.Join(Environment.NewLine, messages);
    public double Progress { get; private set; }

    public MainWindow()
    {
        InitializeComponent(); settings.Mix = Math.Clamp(settings.Mix, 0, 200); DataContext = this;
        RendererStatus = File.Exists(Settings.Renderer) ? "Renderer ready · images stay on this computer" : "Choose the video player executable to set up the renderer";
        elapsed.Tick += (_, _) =>
        {
            if (!IsRunning) return;
            var duration = DateTime.Now - started;
            CurrentStatus = phase + $" · {(int)duration.TotalMinutes}:{duration.Seconds:00} elapsed";
        };
        Jobs.CollectionChanged += (_, _) => Refresh();
        Log("DLSS Neural Batch v0.1.0 ready. Add images, choose Mix and Pano, then start.");
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private void Refresh() { Changed(nameof(CanStart)); Changed(nameof(CanConfigure)); Changed(nameof(IsRunning)); Changed(nameof(Counts)); Changed(nameof(PauseText)); }
    private void Log(string text)
    {
        messages.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + (text.Length > 4096 ? text[..4096] : text));
        while (messages.Count > 200) messages.RemoveAt(0);
        Changed(nameof(LogText));
    }
    private void LogChanged(object sender, TextChangedEventArgs e) => ActivityLog.ScrollToEnd();

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = Jobs.Select(job => job.InputPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                IEnumerable<string> files = Directory.Exists(path) ? Directory.EnumerateFiles(path).OrderBy(file => file, StringComparer.OrdinalIgnoreCase) : new[] { path };
                foreach (var file in files)
                {
                    if (!File.Exists(file) || !ImageFiles.Extensions.Contains(Path.GetExtension(file))) continue;
                    var full = Path.GetFullPath(file);
                    if (!existing.Add(full)) continue;
                    var job = new ImageJob(full);
                    job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageJob.State)) Refresh(); };
                    Jobs.Add(job); added++;
                }
            }
            catch (Exception error) { Log($"Could not add {path}: {error.Message}"); }
        }
        if (added > 0) Log($"Added {added} image{(added == 1 ? "" : "s")} to the queue.");
    }
    private void AddImages(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add images to the queue", Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }
    private void AddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add images from a folder" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FolderNames);
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); }
    private void RemoveSelected(object sender, RoutedEventArgs e) { foreach (var job in QueueGrid.SelectedItems.Cast<ImageJob>().ToArray()) if (job.State != JobState.Running) Jobs.Remove(job); }
    private void ClearFinished(object sender, RoutedEventArgs e) { foreach (var job in Jobs.Where(job => job.State == JobState.Done).ToArray()) Jobs.Remove(job); }
    private void RetryFailed(object sender, RoutedEventArgs e)
    {
        foreach (var job in Jobs.Where(job => job.State is JobState.Failed or JobState.Stopped)) { job.State = JobState.Queued; job.Status = "Waiting"; }
        Refresh();
    }
    private void ChooseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save processed PNG images to", InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dialog.ShowDialog(this) == true) OutputFolder = dialog.FolderName;
    }
    private void OpenOutput(object sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(OutputFolder)) Process.Start(new ProcessStartInfo(Path.GetFullPath(OutputFolder)) { UseShellExecute = true }); else Log("Choose an output folder or complete a render first."); }
        catch (Exception error) { Log(error.Message); }
    }
    private async void ChooseRenderer(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose DLSSVideoPlayer.exe", Filter = "DLSS Video Player|DLSSVideoPlayer.exe|Executables|*.exe", FileName = "DLSSVideoPlayer.exe" };
        if (File.Exists(settings.PlayerExe)) dialog.InitialDirectory = Path.GetDirectoryName(settings.PlayerExe)!;
        if (dialog.ShowDialog(this) != true) return;
        configuring = true; Refresh();
        try
        {
            await RuntimeInstaller.InstallAsync(dialog.FileName, Path.Combine(AppContext.BaseDirectory, "RuntimeFiles"), Path.GetDirectoryName(Settings.Renderer)!, new Progress<string>(text => { RendererStatus = text; }), CancellationToken.None);
            settings.PlayerExe = dialog.FileName; settings.Save(); processor = null;
            RendererStatus = "Renderer ready · images stay on this computer"; Log("Verified and prepared the local renderer.");
        }
        catch (Exception error) { RendererStatus = File.Exists(Settings.Renderer) ? "Previous renderer retained" : "Renderer setup failed"; Log(error.Message); MessageBox.Show(this, error.Message, "Renderer setup", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { configuring = false; Refresh(); }
    }
    private async void StartQueue(object sender, RoutedEventArgs e)
    {
        if (!CanStart) return;
        try
        {
            if (!Path.IsPathFullyQualified(OutputFolder)) throw new IOException("Choose a full output folder path.");
            Directory.CreateDirectory(OutputFolder); settings.Save();
        }
        catch (Exception error) { Log(error.Message); return; }
        var options = new BatchOptions(Mix, Pano, Path.GetFullPath(OutputFolder));
        processor ??= new(Settings.Renderer, Path.Combine(Settings.Root, "Work"));
        cancellation = new(); started = DateTime.Now; elapsed.Start(); Progress = 0; Refresh();
        Log($"Queue started · Mix {Mix}% · Pano {(Pano ? "On" : "Off")} · PNG output.");
        try
        {
            await queue.RunAsync(Jobs, async (job, token) =>
            {
                phase = job.FileName; CurrentStatus = phase; Log("Processing " + job.InputPath);
                var output = await processor.ProcessAsync(job.InputPath, options, new Progress<JobProgress>(progress =>
                {
                    // Late progress callbacks must not overwrite a completed row.
                    if (job.State != JobState.Running) return;
                    job.Status = progress.Phase;
                    if (progress.Size != null) job.Size = progress.Size;
                    phase = job.FileName + " · " + progress.Phase; CurrentStatus = phase;
                    Log(job.FileName + " · " + progress.Phase);
                }), token);
                Log("Saved " + output); return output;
            }, (job, error) => Log(job.FileName + " failed: " + error.Message), cancellation.Token);
            CurrentStatus = "Queue finished. " + Counts; Progress = 100;
        }
        catch (OperationCanceledException) { CurrentStatus = "Queue stopped. Waiting images remain in the list."; Log("Queue stopped. Retry failed/stopped images when ready."); }
        catch (Exception error) { CurrentStatus = "Queue failed: " + error.Message; Log(error.Message); }
        finally { elapsed.Stop(); cancellation.Dispose(); cancellation = null; Changed(nameof(Progress)); Refresh(); }
    }
    private void PauseQueue(object sender, RoutedEventArgs e)
    {
        if (queue.IsPaused) { queue.Resume(); Log("Queue resumed."); }
        else { queue.Pause(); Log("Queue will pause after the current image."); }
        Refresh();
    }
    private void StopQueue(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CurrentStatus = "Stopping the current image…"; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsRunning || configuring)
        { e.Cancel = true; Log(IsRunning ? "Stop the queue before closing the application." : "Wait for renderer setup to finish before closing."); return; }
        try { settings.Save(); } catch { }
    }
}
