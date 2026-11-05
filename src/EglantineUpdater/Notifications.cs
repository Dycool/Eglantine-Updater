using EglantineUpdater.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace EglantineUpdater;

/// <summary>Windows toast notifications. Button clicks come back via ToastNotificationManagerCompat.OnActivated.</summary>
internal static class Notifications
{
    public const string ActionKey = "action";
    public const string UrlKey = "url";

    public const string Update = "update";
    public const string Skip = "skip";
    public const string MarkCurrent = "markcurrent";
    public const string Launch = "launch";
    public const string OpenUrl = "open";
    public const string Status = "status";

    public static void UpdateAvailable(ReleaseInfo release, bool installedUnknown)
    {
        var builder = new ToastContentBuilder()
            .AddArgument(ActionKey, OpenUrl)
            .AddArgument(UrlKey, release.Url)
            .AddText("New Eglantine build available")
            .AddText(release.Name)
            .AddText(installedUnknown
                ? $"Published {release.Published.LocalDateTime:MMM d, h:mm tt}. Your installed build is unknown."
                : $"Published {release.Published.LocalDateTime:MMM d, yyyy h:mm tt}")
            .AddButton(new ToastButton().SetContent("Update now").AddArgument(ActionKey, Update))
            .AddButton(new ToastButton().SetContent("Skip this build").AddArgument(ActionKey, Skip));

        if (installedUnknown)
            builder.AddButton(new ToastButton().SetContent("I have this build").AddArgument(ActionKey, MarkCurrent));

        Show(builder);
    }

    public static void Updated(ReleaseInfo release) =>
        Show(new ToastContentBuilder()
            .AddArgument(ActionKey, Launch)
            .AddText("Eglantine updated")
            .AddText($"{release.Name} is installed.")
            .AddButton(new ToastButton().SetContent("Launch Eglantine").AddArgument(ActionKey, Launch)));

    public static void UpdateFailed(string error) =>
        Show(new ToastContentBuilder()
            .AddArgument(ActionKey, Status)
            .AddText("Eglantine update failed")
            .AddText(error)
            .AddButton(new ToastButton().SetContent("Try again").AddArgument(ActionKey, Update)));

    public static void Info(string title, string message) =>
        Show(new ToastContentBuilder()
            .AddArgument(ActionKey, Status)
            .AddText(title)
            .AddText(message));

    private static void Show(ToastContentBuilder builder)
    {
        try
        {
            builder.Show();
        }
        catch (Exception ex)
        {
            Log.Write($"Toast failed: {ex.Message}");
        }
    }
}
