# Eglantine Launcher

Place `EglantineLauncher.exe` next to `eglantine.exe`, or in an empty writable folder for a fresh install. Open the launcher to check for updates, install the latest matching Windows x64 PGO release, and start Eglantine. Arguments passed to the launcher are forwarded to Eglantine.

The launcher checks GitHub when opened. Prereleases are eligible, as in the original updater. Downloads must match the release asset's SHA-256 digest and size before extraction. This checks the download against GitHub's metadata. The GitHub repository remains the source of trust.

Updates replace files included in the release. Files absent from the release are left alone, and packages that touch the portable `user` directory are rejected. The launcher stages updates in `.eglantine-launcher`, backs up the files it will replace, and rolls back on failure or on the next launch after an interrupted installation. If an update is unavailable or fails and the existing install is usable, the launcher offers a warning and opens the existing build. It leaves a running Eglantine process alone.

The old tray app is a separate executable. If you used it before, use its existing `--uninstall` command to clear its startup registration and settings.

## Build

Windows x64 with the .NET 8 SDK:

```powershell
dotnet publish src/EglantineUpdater -c Release -p:PublishProfile=win-x64
```

The resulting single-file executable requires the x64 .NET 8 Desktop Runtime.
