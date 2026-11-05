namespace EglantineUpdater;

/// <summary>Message boxes and folder pickers that open in front even though the app has no window.</summary>
internal static class Dialogs
{
    private static Form CreateOwner()
    {
        var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.CenterScreen,
        };
        owner.Show();
        owner.Activate();
        return owner;
    }

    public static DialogResult Message(string text, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        using var owner = CreateOwner();
        return MessageBox.Show(owner, text, Program.AppName, buttons, icon);
    }

    public static bool Confirm(string text, MessageBoxIcon icon = MessageBoxIcon.Question) =>
        Message(text, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

    public static string? PickFolder(string description, string? initial)
    {
        using var owner = CreateOwner();
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
