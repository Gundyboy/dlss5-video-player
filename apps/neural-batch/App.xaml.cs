using System.Windows;

namespace NeuralBatch;

internal partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\Reality3D.DLSSNeuralBatch", out var ownsInstance);
        var headless = e.Args.Contains("--validate-runtime") || e.Args.Contains("--configure-runtime") || e.Args.Contains("--check-idle");
        string Argument(string key)
        {
            var index = Array.IndexOf(e.Args, key);
            if (index < 0 || index + 1 == e.Args.Length) throw new ArgumentException("Missing argument: " + key);
            return e.Args[index + 1];
        }
        if (headless)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string? report = null;
            try
            {
                report = Argument("--report");
                if (!ownsInstance) throw new IOException("Close DLSS Neural Batch before updating the renderer.");
                if (e.Args.Contains("--check-idle")) { File.WriteAllText(report, "Application is closed."); Shutdown(0); return; }
                var payload = Path.Combine(AppContext.BaseDirectory, "RuntimeFiles");
                var configure = e.Args.Contains("--configure-runtime");
                var player = Argument(configure ? "--configure-runtime" : "--validate-runtime");
                if (configure)
                {
                    await RuntimeInstaller.InstallAsync(player, payload, Path.GetDirectoryName(Settings.Renderer)!, null, CancellationToken.None);
                    var settings = Settings.Load(); settings.PlayerExe = player; settings.Save();
                }
                else await RuntimeInstaller.ValidateAsync(player, payload, null, CancellationToken.None);
                File.WriteAllText(report, configure ? "Renderer installed successfully." : "Renderer validation passed.");
                Shutdown(0);
            }
            catch (Exception error) { try { if (report != null) File.WriteAllText(report, error.Message); } catch { } Shutdown(1); }
            return;
        }
        if (!ownsInstance) { MessageBox.Show("DLSS Neural Batch is already open.", "DLSS Neural Batch"); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args.Length > 0) window.AddPaths(e.Args);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
