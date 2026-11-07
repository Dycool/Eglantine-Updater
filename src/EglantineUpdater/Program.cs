using System.Diagnostics;
using EglantineUpdater.Core;

namespace EglantineUpdater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = AppContext.BaseDirectory;
        using var window = new Form
        {
            Text = "Eglantine Launcher",
            ClientSize = new Size(440, 110),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ControlBox = false
        };
        using var status = new Label
        {
            AutoSize = false, Location = new Point(20, 19), Size = new Size(400, 24),
            Text = "Checking for updates."
        };
        using var bar = new ProgressBar
        {
            Location = new Point(20, 54), Size = new Size(400, 18),
            Style = ProgressBarStyle.Marquee
        };
        window.Controls.AddRange([status, bar]);

        window.Shown += async (_, _) =>
        {
            try
            {
                var progress = new Progress<string>(message => status.Text = message);
                await Task.Run(() => UpdateAndLaunch(root, args, progress));
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex.Message, window.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                window.Close();
            }
        };
        Application.Run(window);
    }

    private static async Task UpdateAndLaunch(string root, string[] args, IProgress<string> progress)
    {
        var exe = Installer.CheckedPath(root, Installer.Emulator);
        var state = Path.Combine(root, Installer.StateFolder);
        Installer.EnsurePlainDirectory(state);
        using var updateLock = new FileStream(Installer.CheckedPath(state, "launcher.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var running = IsRunning(exe);
        if (running)
        {
            if (Installer.HasPendingUpdate(state))
                throw new IOException("Close Eglantine before the launcher recovers the previous update.");
            return;
        }

        Installer.Recover(root, state);
        try
        {
            var release = await GitHubReleases.GetLatestAsync();
            var installed = Installer.CheckedPath(state, "installed");
            if (File.Exists(exe) && File.Exists(installed) && File.ReadAllText(installed) == release.Identity)
            {
                progress.Report("Eglantine is up to date.");
            }
            else
            {
                if (IsRunning(exe)) return;
                await Installer.InstallAsync(release, root, state, progress, () => IsRunning(exe));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A failed recovery must prevent launching a potentially mixed installation.
            if (!File.Exists(exe) || Installer.HasPendingUpdate(state)) throw;
            MessageBox.Show($"Update skipped. The installed build will start.\n\n{ex.Message}",
                "Eglantine Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (IsRunning(exe)) return;
        progress.Report("Starting Eglantine.");
        var start = new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }

    private static bool IsRunning(string exe)
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("eglantine"))
        {
            using (process)
            {
                try
                {
                    running |= string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException) { /* The process exited. */ }
                catch (System.ComponentModel.Win32Exception) { running = true; }
            }
        }
        return running;
    }
}
