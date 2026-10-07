using System.Globalization;
using System.Resources;

namespace Compositor.Desktop.Localization;

/// <summary>
/// Looks up the Desktop application's display text. The document model deliberately does not depend on this
/// type: project data, enum spellings and shortcut identifiers stay language-neutral and cross-platform.
/// </summary>
public sealed class LocalizationManager
{
    public const string EnglishCulture = "en-US";
    public const string TraditionalChineseCulture = "zh-TW";

    private static readonly ResourceManager Resources = new(
        "Compositor.Desktop.Localization.Strings", typeof(LocalizationManager).Assembly);

    private readonly LanguagePreferences? _preferences;
    private CultureInfo _culture;

    public LocalizationManager(string? savedLanguage = null, CultureInfo? systemCulture = null,
        LanguagePreferences? preferences = null)
    {
        _preferences = preferences;
        _culture = CultureFor(savedLanguage, systemCulture ?? DefaultSystemCulture());
    }

    /// <summary>The culture currently used for new Desktop controls and messages.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Raised after a persisted language choice changes, for future live control rebinding.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Reads a resource by key, falling back to neutral English, then the centralized safe display fallback, and
    /// finally the key itself. A missing translation must never stop the editor from opening.
    /// </summary>
    public string Get(string key, params object?[] arguments)
    {
        var value = Translation(key)
            ?? Resources.GetString(key, CultureInfo.GetCultureInfo(EnglishCulture))
            ?? ResourceFallbacks.Get(key, _culture)
            ?? ResourceFallbacks.Get(key, CultureInfo.GetCultureInfo(EnglishCulture))
            ?? key;
        if (arguments.Length == 0) return value;
        try
        {
            return string.Format(_culture, value, arguments);
        }
        catch (FormatException)
        {
            // Bad localization data should be visible but must not make a menu or dialog unusable.
            return value;
        }
    }

    private string? Translation(string key)
    {
        // ResourceManager normally falls back to the neutral resource by itself. Inspect the exact zh-TW set
        // first so the required English fallback remains explicit and has a deterministic order.
        if (!string.Equals(_culture.Name, TraditionalChineseCulture, StringComparison.OrdinalIgnoreCase))
        {
            return Resources.GetString(key, _culture);
        }

        try
        {
            return Resources.GetResourceSet(_culture, createIfNotExists: true, tryParents: false)
                ?.GetString(key, ignoreCase: false);
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }

    /// <summary>Changes the language preference. Existing programmatic controls remain as built until restart.</summary>
    public void SetLanguage(string cultureName, bool persist = true)
    {
        var next = CultureFor(cultureName, CultureInfo.GetCultureInfo(EnglishCulture));
        var changed = !string.Equals(next.Name, _culture.Name, StringComparison.OrdinalIgnoreCase);
        _culture = next;
        if (persist && _preferences is not null)
        {
            _preferences.Language = next.Name;
            _preferences.Save();
        }
        if (!changed) return;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CultureInfo CultureFor(string? preferred, CultureInfo systemCulture)
    {
        if (string.Equals(preferred, TraditionalChineseCulture, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(preferred) && string.Equals(systemCulture.Name, TraditionalChineseCulture,
                StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo(TraditionalChineseCulture);
        }
        return CultureInfo.GetCultureInfo(EnglishCulture);
    }

    private static CultureInfo DefaultSystemCulture()
    {
        // Windows normally keeps these the same. Respecting a Taiwanese regional culture as well makes the
        // first-launch rule match the operating system setting even when its display language was not changed.
        return string.Equals(CultureInfo.CurrentUICulture.Name, TraditionalChineseCulture,
                StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.CurrentUICulture
            : string.Equals(CultureInfo.CurrentCulture.Name, TraditionalChineseCulture,
                StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.CurrentCulture
                : CultureInfo.CurrentUICulture;
    }
}

/// <summary>Application-wide localization access for controls that are built directly in C#.</summary>
public static class L
{
    private static LocalizationManager _current = new();

    public static LocalizationManager Current => _current;

    public static string Get(string key, params object?[] arguments) => _current.Get(key, arguments);

    /// <summary>Sets up the manager before DesktopApp creates its first window.</summary>
    public static void Initialize()
    {
        var preferences = LanguagePreferences.Load();
        _current = new LocalizationManager(preferences.Language, preferences: preferences);
    }

    /// <summary>Lets localization tests exercise a manager without touching per-user preferences.</summary>
    public static void Use(LocalizationManager manager) => _current = manager;
}
