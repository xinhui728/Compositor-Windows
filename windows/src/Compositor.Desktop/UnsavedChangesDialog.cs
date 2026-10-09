using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Compositor.Desktop;

/// <summary>The user's decision when closing a document with changes that have not been saved.</summary>
internal enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// A three-way confirmation for a changed document. The caller supplies all visible text so its labels remain
/// centralized in the localization resources rather than becoming another source of UI strings here.
/// </summary>
internal sealed class UnsavedChangesDialog : DialogWindow
{
    private UnsavedChangesChoice _choice = UnsavedChangesChoice.Cancel;

    private UnsavedChangesDialog(string title, string message, string save, string discard, string cancel)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancelButton = new Button { Content = cancel, IsCancel = true };
        var discardButton = new Button { Content = discard };
        var saveButton = new Button { Content = save, IsDefault = true };
        cancelButton.Click += (_, _) => Close();
        discardButton.Click += (_, _) => Choose(UnsavedChangesChoice.Discard);
        saveButton.Click += (_, _) => Choose(UnsavedChangesChoice.Save);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, discardButton, saveButton },
                },
            },
        };
        Opened += (_, _) => cancelButton.Focus();
    }

    private void Choose(UnsavedChangesChoice choice)
    {
        _choice = choice;
        Close();
    }

    /// <summary>
    /// Shows the confirmation. Dismissing it with the window close button or Escape returns <see
    /// cref="UnsavedChangesChoice.Cancel"/>.
    /// </summary>
    public static async Task<UnsavedChangesChoice> Ask(Window owner, string title, string message, string save,
        string discard, string cancel)
    {
        var dialog = new UnsavedChangesDialog(title, message, save, discard, cancel);
        await dialog.ShowDialog(owner);
        return dialog._choice;
    }
}
