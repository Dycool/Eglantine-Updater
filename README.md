# Eglantine Updater

A Windows tray app (C#, .NET 8, WinForms) that watches
https://github.com/Djihads80/Eglantine/releases. When a new build is out, it shows a Windows
toast notification. It can then download `eglantine-windows-x86_64-pgo.zip` and unzip it
into your Eglantine folder.

## Build

**Visual Studio 2022** (17.8 or later, with the ".NET desktop development" workload):

1. Open `EglantineUpdater.sln`.
2. Build → Build Solution. NuGet restores `Microsoft.Toolkit.Uwp.Notifications` automatically.
3. Press F5 to run. Look for the pink flower icon in the system tray (it may be under the `^` overflow arrow).

**Single .exe to share** (includes the .NET runtime, so nothing needs installing on the target PC):

- In Visual Studio: right-click the project → Publish → choose the `win-x64` profile → Publish.
- Or from a terminal:
  ```
  dotnet publish src\EglantineUpdater -p:PublishProfile=win-x64
  ```
- Output: `src\EglantineUpdater\bin\publish\win-x64\EglantineUpdater.exe`

Put the exe somewhere permanent, such as `%LOCALAPPDATA%\Programs\EglantineUpdater`, before
the first run. "Start with Windows" and toast registration remember the exe's location.

## What it does

- **First run**: asks for your Eglantine folder (the one containing `eglantine.exe`). If
  Eglantine is running, it suggests that folder. It also turns on **Start with Windows**
  (an `HKCU\...\Run` entry that starts it with `--background`).
- **Checks GitHub** 10 seconds after starting, then every 6 hours by default (1–24 h in
  Settings). Pre-releases count. The "latest" build is the newest release that has a file
  matching `^eglantine-windows-x86_64-pgo.*\.zip$`, so builds without that zip (for example
  "AMD Experimental") are skipped.
- **Toast notification**: has **Update now**, **Skip this build** and, while your installed
  build is unknown, **I have this build**. Clicking the toast body opens the release page.
  Buttons work even if the updater was closed: Windows relaunches it.
- **Updating**: asks before closing Eglantine if it's running, then downloads with progress
  shown in the tray tooltip and extracts. If the zip wraps everything in one top-level
  folder, it unwraps it. It copies the files over the install, replacing program files and
  leaving everything else (your user data) alone. When done, it shows a toast with
  **Launch Eglantine**.
- **Tray menu**: installed and latest build, Update / Check / Skip, Open release page,
  Launch Eglantine, Open folder, and Settings (folder, interval, Start with Windows, log,
  settings file). Double-click the icon to check right away.

## Files

| Path | Purpose |
|---|---|
| `%APPDATA%\EglantineUpdater\config.json` | Settings. `AssetPattern` is the regex for the file to download. |
| `%APPDATA%\EglantineUpdater\updater.log` | Log |

## Uninstall

Run `EglantineUpdater.exe --uninstall`. This removes the startup entry, the toast
registration and the settings. Then delete the exe. Eglantine itself is not touched.

## Project layout

```
EglantineUpdater.sln
src/EglantineUpdater/
  Program.cs            entry point, single instance, toast activation
  TrayApp.cs            tray icon, menu, schedule, update flow
  Notifications.cs      toast content (Microsoft.Toolkit.Uwp.Notifications)
  Dialogs.cs            message boxes / folder picker that open in front
  StartupManager.cs     Start with Windows (HKCU Run key)
  Core/GitHubReleases.cs  GitHub API + picking the latest matching build
  Core/Installer.cs       download, extract, copy over install
  Core/AppConfig.cs       settings
  Core/Log.cs             log file
```
