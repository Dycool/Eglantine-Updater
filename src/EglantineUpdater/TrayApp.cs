using System.Diagnostics;
using EglantineUpdater.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace EglantineUpdater;

/// <summary>The tray icon, its menu, the check schedule and the update flow. Everything runs on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly AppConfig _config = AppConfig.Load();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _scheduler;
    private readonly Icon _icon;

    private readonly ToolStripMenuItem _miInstalled, _miLatest, _miUpdate, _miCheck, _miSkip,
        _miLaunch, _miFolder, _miMark, _miStartup, _miInterval;

    private ReleaseInfo? _latest;
    private bool _checking, _updating;
    private string? _afterCheck;      // action waiting for the first successful check
    private DateTimeOffset _nextCheck;

    public TrayApp(bool background)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("No UI synchronization context.");
        _icon = (Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null) ?? SystemIcons.Application;

        var menu = new ContextMenuStrip();
        var title = Add(menu.Items, Program.AppName, null);
        title.Enabled = false;
        title.Font = new Font(title.Font, FontStyle.Bold);
        _miInstalled = Add(menu.Items, "Installed: unknown", null); _miInstalled.Enabled = false;
        _miLatest = Add(menu.Items, "Latest: not checked yet", null); _miLatest.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        _miUpdate = Add(menu.Items, "Update now", (_, _) => StartUpdate());
        _miCheck = Add(menu.Items, "Check for updates", (_, _) => StartCheck(manual: true));
        _miSkip = Add(menu.Items, "Skip this build", (_, _) => Skip());
        Add(menu.Items, "Open release page", (_, _) => OpenUrl(_latest?.Url ?? GitHubReleases.ReleasesPage));
        menu.Items.Add(new ToolStripSeparator());
        _miLaunch = Add(menu.Items, "Launch Eglantine", (_, _) => LaunchEglantine());
        _miFolder = Add(menu.Items, "Open Eglantine folder", (_, _) => { if (InstallFolder is { } f) OpenUrl(f); });

        var settings = Add(menu.Items, "Settings", null);
        Add(settings.DropDownItems, "Change Eglantine folder...", (_, _) => SelectInstallFolder(askIfCurrent: true));
        _miMark = Add(settings.DropDownItems, "Mark latest build as installed", (_, _) => MarkCurrent());
        _miInterval = Add(settings.DropDownItems, "Check every", null);
        foreach (var (minutes, label) in new[] { (60, "1 hour"), (180, "3 hours"), (360, "6 hours"), (720, "12 hours"), (1440, "24 hours") })
        {
            var item = Add(_miInterval.DropDownItems, label, (_, _) =>
            {
                _config.CheckIntervalMinutes = minutes;
                _config.Save();
                _nextCheck = DateTimeOffset.Now.AddMinutes(minutes);
                RefreshMenu();
            });
            item.Tag = minutes;
        }
        _miStartup = Add(settings.DropDownItems, "Start with Windows", (_, _) => ToggleStartup());
        Add(settings.DropDownItems, "Open log", (_, _) => { if (File.Exists(Log.FilePath)) OpenUrl(Log.FilePath); });
        Add(settings.DropDownItems, "Open settings file (asset pattern)", (_, _) => { _config.Save(); OpenUrl(AppConfig.FilePath); });
        menu.Items.Add(new ToolStripSeparator());
        Add(menu.Items, "Exit", (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = Program.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += (_, _) => StartCheck(manual: true);
        menu.Opening += (_, _) => RefreshMenu();

        // First check shortly after start, unless the last one was recent.
        _nextCheck = DateTimeOffset.Now.AddSeconds(10);
        if (_config.LastCheck is { } last && last.AddMinutes(_config.CheckIntervalMinutes) > _nextCheck)
            _nextCheck = last.AddMinutes(_config.CheckIntervalMinutes);

        _scheduler = new System.Windows.Forms.Timer { Interval = 30_000 };
        _scheduler.Tick += (_, _) => { if (!_checking && !_updating && DateTimeOffset.Now >= _nextCheck) StartCheck(manual: false); };
        _scheduler.Start();

        RefreshMenu();
        Post(() => FirstRun(background));
    }

    public void Post(Action action) => _ui.Post(_ => action(), null);

    // ------------------------------------------------------------------ first run

    private void FirstRun(bool background)
    {
        if (!_config.StartupConfigured)
        {
            try { StartupManager.SetEnabled(true); } catch (Exception ex) { Log.Write($"Startup registration failed: {ex.Message}"); }
            _config.StartupConfigured = true;
            _config.Save();
        }

        if (string.IsNullOrEmpty(_config.InstallPath) && !background)
        {
            Notifications.Info("Eglantine Updater is running",
                "It lives in the system tray and will tell you when a new Eglantine build is out.");
            if (SelectInstallFolder(askIfCurrent: false, firstRun: true)) StartCheck(manual: false);
        }
    }

    // ------------------------------------------------------------------ toast + signal handling

    public void HandleToast(ToastArguments args)
    {
        if (!args.TryGetValue(Notifications.ActionKey, out string action)) action = Notifications.Status;
        args.TryGetValue(Notifications.UrlKey, out string url);
        RunAction(action, url);
    }

    private void RunAction(string action, string? url = null)
    {
        switch (action)
        {
            case Notifications.Update: StartUpdate(); break;
            case Notifications.Skip: Skip(); break;
            case Notifications.MarkCurrent: MarkCurrent(); break;
            case Notifications.Launch: LaunchEglantine(); break;
            case Notifications.OpenUrl:
                OpenUrl(string.IsNullOrEmpty(url) ? GitHubReleases.ReleasesPage : url);
                break;
            default: ShowStatus(); break;
        }
    }

    public void ShowStatus()
    {
        var installed = string.IsNullOrEmpty(_config.InstalledName) ? "unknown" : _config.InstalledName;
        var latest = _latest is null ? "not checked yet" : $"{_latest.Name} [{_latest.Tag}]";
        var folder = string.IsNullOrEmpty(_config.InstallPath) ? "not set" : _config.InstallPath;
        _tray.ShowBalloonTip(8000, Program.AppName, $"Installed: {installed}\nLatest: {latest}\nFolder: {folder}", ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ checking

    private async void StartCheck(bool manual)
    {
        if (_checking) return;
        _checking = true;
        SetTrayText($"{Program.AppName} - checking...");
        RefreshMenu();
        try
        {
            var latest = await GitHubReleases.GetLatestAsync(_config.AssetPattern);
            _nextCheck = DateTimeOffset.Now.AddMinutes(_config.CheckIntervalMinutes);
            if (latest is null)
            {
                Log.Write($"No release has an asset matching '{_config.AssetPattern}'");
                if (manual) Notifications.Info("No matching build found", $"No release contains a file matching {_config.AssetPattern}");
                return;
            }

            _latest = latest;
            _config.LastCheck = DateTimeOffset.Now;
            _config.Save();
            Log.Write($"Latest: {latest.Tag} ({latest.AssetName}); installed: '{_config.InstalledTag}'");

            if (_config.IsUpdateAvailable(latest)) NotifyUpdate(force: manual);
            else if (manual) Notifications.Info("Eglantine is up to date", $"{latest.Name} [{latest.Tag}]");
        }
        catch (Exception ex)
        {
            Log.Write($"Check failed: {ex.Message}");
            _nextCheck = DateTimeOffset.Now.AddMinutes(30); // retry sooner after a failure
            if (manual) Notifications.Info("Update check failed", ex.Message);
        }
        finally
        {
            _checking = false;
            RefreshMenu();
            var next = _afterCheck;
            _afterCheck = null;
            if (next is not null && _latest is not null) RunAction(next);
        }
    }

    private void NotifyUpdate(bool force)
    {
        if (_latest is null || !_config.IsUpdateAvailable(_latest)) return;
        if (!force && (_config.SkippedTag == _latest.Tag || _config.LastNotifiedTag == _latest.Tag)) return;
        _config.LastNotifiedTag = _latest.Tag;
        _config.Save();
        Notifications.UpdateAvailable(_latest, installedUnknown: string.IsNullOrEmpty(_config.InstalledTag));
    }

    // ------------------------------------------------------------------ updating

    private async void StartUpdate()
    {
        if (_updating) return;
        if (_latest is null)
        {
            _afterCheck = Notifications.Update;
            StartCheck(manual: true);
            return;
        }

        var folder = InstallFolder;
        if (folder is null)
        {
            if (!SelectInstallFolder(askIfCurrent: false)) return;
            Directory.CreateDirectory(_config.InstallPath);
            folder = InstallFolder;
            if (folder is null) return;
        }
        if (!CloseRunningEglantine(folder)) return;

        var release = _latest;
        _updating = true;
        RefreshMenu();
        Log.Write($"Updating to {release.Tag} from {release.AssetUrl} into {folder}");

        var progress = new Progress<InstallProgress>(p =>
            SetTrayText(p.Percent >= 0 ? $"{Program.AppName} - {p.Stage} {p.Percent}%" : $"{Program.AppName} - {p.Stage}..."));
        try
        {
            await Installer.InstallAsync(release, folder, progress);
            _config.MarkInstalled(release);
            _config.SkippedTag = "";
            _config.Save();
            Log.Write($"Installed {release.Tag}");
            Notifications.Updated(release);
        }
        catch (Exception ex)
        {
            Log.Write($"Update failed: {ex}");
            Notifications.UpdateFailed(ex.Message);
        }
        finally
        {
            _updating = false;
            RefreshMenu();
        }
    }

    private static bool CloseRunningEglantine(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var running = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { running.Add(p); continue; }
            }
            catch { /* access denied for system processes */ }
            p.Dispose();
        }
        if (running.Count == 0) return true;

        try
        {
            if (!Dialogs.Confirm("Eglantine is running. It must be closed to install the update.\n\nClose it now?", MessageBoxIcon.Warning))
                return false;
            foreach (var p in running) { try { p.CloseMainWindow(); } catch { } }
            foreach (var p in running)
            {
                try { if (!p.WaitForExit(10_000)) { p.Kill(entireProcessTree: true); p.WaitForExit(5_000); } } catch { }
            }
            return true;
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
    }

    // ------------------------------------------------------------------ settings actions

    private string? InstallFolder =>
        !string.IsNullOrEmpty(_config.InstallPath) && Directory.Exists(_config.InstallPath)
            ? Path.GetFullPath(_config.InstallPath)
            : null;

    private static string? FindRunningEglantineFolder()
    {
        foreach (var p in Process.GetProcessesByName("eglantine"))
        {
            using (p)
            {
                try { if (p.MainModule?.FileName is { } path) return Path.GetDirectoryName(path); } catch { }
            }
        }
        return null;
    }

    private bool SelectInstallFolder(bool askIfCurrent, bool firstRun = false)
    {
        var detected = FindRunningEglantineFolder();
        string? path = null;
        if (firstRun && detected is not null &&
            Dialogs.Confirm($"Eglantine is running from:\n\n{detected}\n\nUse this folder for updates?"))
        {
            path = detected;
        }

        path ??= Dialogs.PickFolder(
            "Select the folder where Eglantine is installed (the folder containing eglantine.exe). Pick an empty folder for a fresh install.",
            string.IsNullOrEmpty(_config.InstallPath) ? detected : _config.InstallPath);
        if (path is null) return false;

        var hasExe = File.Exists(Path.Combine(path, Installer.EglantineExe));
        if (!hasExe && !Dialogs.Confirm($"eglantine.exe was not found in:\n\n{path}\n\nUse this folder anyway? The latest build will be installed here.", MessageBoxIcon.Warning))
            return false;

        var changed = !string.Equals(path, _config.InstallPath, StringComparison.OrdinalIgnoreCase);
        _config.InstallPath = path;
        if (changed) _config.ClearInstalled();
        _config.Save();

        if (askIfCurrent && changed && hasExe && _latest is not null)
        {
            if (Dialogs.Confirm($"Is the Eglantine build in this folder already the latest one?\n\n{_latest.Name}  [{_latest.Tag}]\n\nYes = mark as up to date.  No = offer to install it."))
                MarkCurrent();
            else
                NotifyUpdate(force: true);
        }
        RefreshMenu();
        return true;
    }

    private void Skip()
    {
        if (_latest is null) { _afterCheck = Notifications.Skip; StartCheck(manual: false); return; }
        _config.SkippedTag = _latest.Tag;
        _config.Save();
        RefreshMenu();
    }

    private void MarkCurrent()
    {
        if (_latest is null) { _afterCheck = Notifications.MarkCurrent; StartCheck(manual: false); return; }
        _config.MarkInstalled(_latest);
        _config.Save();
        RefreshMenu();
    }

    private void LaunchEglantine()
    {
        var exe = InstallFolder is { } f ? Path.Combine(f, Installer.EglantineExe) : null;
        if (exe is not null && File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
        else
            Dialogs.Message("eglantine.exe was not found. Set your Eglantine folder from the tray menu (Settings).",
                icon: MessageBoxIcon.Warning);
    }

    private void ToggleStartup()
    {
        try { StartupManager.SetEnabled(!StartupManager.IsEnabled); }
        catch (Exception ex) { Dialogs.Message($"Could not change the startup setting: {ex.Message}", icon: MessageBoxIcon.Error); }
        RefreshMenu();
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {target}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ UI state

    private void RefreshMenu()
    {
        _miInstalled.Text = string.IsNullOrEmpty(_config.InstalledName)
            ? "Installed: unknown"
            : $"Installed: {_config.InstalledName} [{_config.InstalledTag}]";
        _miLatest.Text = _latest is null ? "Latest: not checked yet" : $"Latest: {_latest.Name} [{_latest.Tag}]";

        var available = _config.IsUpdateAvailable(_latest);
        (_miUpdate.Text, _miUpdate.Enabled) = (_updating, available, _latest) switch
        {
            (true, _, _) => ("Updating...", false),
            (_, true, { } l) => ($"Update to {l.Tag}", true),
            (_, _, { } l) => ($"Reinstall {l.Tag}", true),
            _ => ("Install latest build", true),
        };
        _miCheck.Enabled = !_checking;
        _miSkip.Visible = available && _latest is not null && _config.SkippedTag != _latest.Tag;
        var hasFolder = InstallFolder is not null;
        _miLaunch.Enabled = hasFolder;
        _miFolder.Enabled = hasFolder;
        _miMark.Enabled = available;
        _miStartup.Checked = StartupManager.IsEnabled;
        foreach (ToolStripMenuItem item in _miInterval.DropDownItems)
            item.Checked = item.Tag is int m && m == _config.CheckIntervalMinutes;

        if (!_checking && !_updating)
        {
            if (available && _latest is not null && _config.SkippedTag != _latest.Tag)
                SetTrayText($"{Program.AppName} - update available ({_latest.Tag})");
            else if (_latest is not null)
                SetTrayText($"{Program.AppName} - up to date");
            else
                SetTrayText(Program.AppName);
        }
    }

    private void SetTrayText(string text) => _tray.Text = text.Length > 127 ? text[..124] + "..." : text;

    private static ToolStripMenuItem Add(ToolStripItemCollection items, string text, EventHandler? onClick)
    {
        var item = new ToolStripMenuItem(text);
        if (onClick is not null) item.Click += onClick;
        items.Add(item);
        return item;
    }

    protected override void ExitThreadCore()
    {
        _scheduler.Stop();
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scheduler.Dispose();
            _tray.Dispose();
            if (!ReferenceEquals(_icon, SystemIcons.Application)) _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
