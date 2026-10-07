using System.Text.Json;

namespace Compositor.Desktop.Localization;

/// <summary>Desktop-only preferences which are personal to this Windows installation, never part of a project.</summary>
public sealed class LanguagePreferences
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor", "preferences.json");

    public string? Language { get; set; }

    public string Path { get; }

    public LanguagePreferences(string? path = null) => Path = path ?? DefaultPath;

    public static LanguagePreferences Load(string? path = null)
    {
        var preferences = new LanguagePreferences(path);
        try
        {
            if (!File.Exists(preferences.Path)) return preferences;
            var saved = JsonSerializer.Deserialize<LanguagePreferencesData>(File.ReadAllText(preferences.Path), Json);
            preferences.Language = saved?.Language;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A language choice is a convenience; a bad preference file must not stop the app opening.
        }
        return preferences;
    }

    public void Save()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (folder is not null) Directory.CreateDirectory(folder);
            File.WriteAllText(Path, JsonSerializer.Serialize(new LanguagePreferencesData { Language = Language }, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Preferences are optional and should not turn a menu choice into an application error.
        }
    }

    private sealed class LanguagePreferencesData
    {
        public string? Language { get; set; }
    }
}
