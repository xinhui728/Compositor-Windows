using System.Globalization;
using Compositor.Core.IO;
using Compositor.Desktop.Localization;

namespace Compositor.Desktop.Tests;

/// <summary>Contract tests for the Desktop-only text lookup and language preference boundary.</summary>
public class LocalizationTests
{
    [Fact]
    public void EnglishResourcesAreAvailable()
    {
        var localization = new LocalizationManager(LocalizationManager.EnglishCulture);

        Assert.Equal("_File", localization.Get("Menu.File"));
        Assert.Equal(LocalizationManager.EnglishCulture, localization.Culture.Name);
    }

    [Fact]
    public void TraditionalChineseResourcesAreAvailable()
    {
        var localization = new LocalizationManager(LocalizationManager.TraditionalChineseCulture);

        Assert.Equal("檔案", localization.Get("Menu.File"));
        Assert.Equal(LocalizationManager.TraditionalChineseCulture, localization.Culture.Name);
    }

    [Fact]
    public void TraditionalChineseMissingResourcesUseTheSafeFallback()
    {
        var localization = new LocalizationManager(LocalizationManager.TraditionalChineseCulture);

        // The final fallback is the key itself, so an incomplete future zh-TW satellite resource cannot
        // prevent a control from being created.
        Assert.Equal("Missing.Resource.Key", localization.Get("Missing.Resource.Key"));
    }

    [Fact]
    public void UnsupportedCultureFallsBackToEnglishAndMissingKeysAreSafe()
    {
        var localization = new LocalizationManager("fr-FR");

        Assert.Equal(LocalizationManager.EnglishCulture, localization.Culture.Name);
        Assert.Equal("_File", localization.Get("Menu.File"));
        Assert.Equal("Missing.Resource.Key", localization.Get("Missing.Resource.Key"));
    }

    [Fact]
    public void ParameterizedStringsUseTheSelectedCultureResource()
    {
        var english = new LocalizationManager(LocalizationManager.EnglishCulture);
        var traditionalChinese = new LocalizationManager(LocalizationManager.TraditionalChineseCulture);

        Assert.Equal("Tolerance 42", english.Get("ToolOptions.Value.Tolerance", 42));
        Assert.Equal("容差 42", traditionalChinese.Get("ToolOptions.Value.Tolerance", 42));
    }

    [Fact]
    public void SystemCultureOnlySelectsTraditionalChineseForTaiwan()
    {
        var taiwan = new LocalizationManager(systemCulture: CultureInfo.GetCultureInfo("zh-TW"));
        var otherChineseCulture = new LocalizationManager(systemCulture: CultureInfo.GetCultureInfo("zh-HK"));

        Assert.Equal(LocalizationManager.TraditionalChineseCulture, taiwan.Culture.Name);
        Assert.Equal(LocalizationManager.EnglishCulture, otherChineseCulture.Culture.Name);
    }

    [Fact]
    public void ChangingLanguageRaisesOneNotificationPerActualChange()
    {
        var localization = new LocalizationManager(LocalizationManager.EnglishCulture);
        var notifications = 0;
        localization.LanguageChanged += (_, _) => notifications++;

        localization.SetLanguage(LocalizationManager.TraditionalChineseCulture, persist: false);
        localization.SetLanguage(LocalizationManager.TraditionalChineseCulture, persist: false);

        Assert.Equal(LocalizationManager.TraditionalChineseCulture, localization.Culture.Name);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void TraditionalChineseDisplaysShortcutNamesWithoutChangingStableIdentifiers()
    {
        var localization = new LocalizationManager(LocalizationManager.TraditionalChineseCulture);

        foreach (var definition in Shortcuts.Definitions)
        {
            var key = $"Shortcut.Command.{definition.ID}";
            var display = localization.Get(key);

            Assert.NotEqual(key, display);
            Assert.DoesNotContain("Shortcut.Command.", display);
            Assert.Contains(':', definition.ID);
        }
    }

    [Fact]
    public void SelectionMaskAndUngroupParityStringsHaveReviewedResourcesInBothCultures()
    {
        var english = new LocalizationManager(LocalizationManager.EnglishCulture);
        var traditionalChinese = new LocalizationManager(LocalizationManager.TraditionalChineseCulture);

        Assert.Equal("_Ungroup Layers", english.Get("Menu.UngroupLayers"));
        Assert.Equal("Reveal _Selection", english.Get("Menu.RevealSelection"));
        Assert.Equal("Hide _Selection", english.Get("Menu.HideSelection"));
        Assert.Equal("取消圖層群組", traditionalChinese.Get("Menu.UngroupLayers"));
        Assert.Equal("顯示選取範圍", traditionalChinese.Get("Menu.RevealSelection"));
        Assert.Equal("隱藏選取範圍", traditionalChinese.Get("Menu.HideSelection"));

        var ungroup = Assert.Single(Shortcuts.Definitions, definition =>
            definition.ID == $"{Shortcuts.Menus}:Ungroup Layers");
        Assert.Equal("G", ungroup.Original.Key);
        Assert.Equal(ShortcutModifiers.Control | ShortcutModifiers.Shift, ungroup.Original.Modifiers);
        Assert.Equal("取消圖層群組", traditionalChinese.Get($"Shortcut.Command.{ungroup.ID}"));
    }

    [Fact]
    public void SelectedLanguageIsPersistedOutsideProjects()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Compositor.Desktop.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "preferences.json");
        try
        {
            var preferences = new LanguagePreferences(path);
            var localization = new LocalizationManager(preferences: preferences);

            localization.SetLanguage(LocalizationManager.TraditionalChineseCulture);

            Assert.Equal(LocalizationManager.TraditionalChineseCulture, LanguagePreferences.Load(path).Language);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
