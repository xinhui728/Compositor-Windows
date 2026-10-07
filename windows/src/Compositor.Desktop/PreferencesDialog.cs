using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Compositor.Desktop;

/// <summary>Small, extensible home for application settings that do not belong to a document.</summary>
internal sealed class PreferencesDialog : DialogWindow
{
    private readonly ComboBox _language = new() { Width = 220 };
    private string? _result;

    private PreferencesDialog()
    {
        Title = L.Get("Preferences.Title");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _language.ItemsSource = new[]
        {
            new LanguageChoice(LocalizationManager.EnglishCulture, L.Get("Language.English")),
            new LanguageChoice(LocalizationManager.TraditionalChineseCulture, L.Get("Language.TraditionalChinese")),
        };
        _language.SelectedIndex = string.Equals(L.Current.Culture.Name, LocalizationManager.TraditionalChineseCulture,
            StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        var ok = new Button { Content = L.Get("Common.OK"), IsDefault = true };
        var cancel = new Button { Content = L.Get("Common.Cancel"), IsCancel = true };
        ok.Click += (_, _) =>
        {
            _result = (_language.SelectedItem as LanguageChoice)?.Culture;
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = L.Get("Preferences.Language"), Width = 100, VerticalAlignment = VerticalAlignment.Center },
                        _language,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
    }

    public static async Task<string?> Ask(Window owner)
    {
        var dialog = new PreferencesDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private sealed record LanguageChoice(string Culture, string Name)
    {
        public override string ToString() => Name;
    }
}
