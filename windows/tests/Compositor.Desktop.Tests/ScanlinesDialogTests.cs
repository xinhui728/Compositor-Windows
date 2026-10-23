using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Desktop.Localization;

namespace Compositor.Desktop.Tests;

public sealed class ScanlinesDialogTests
{
    [Fact]
    public void CrtControlsReplaceTheInapplicableRowsInBothLanguages()
    {
        DesktopTestHost.EnsureInitialized();
        var was = L.Current;
        try
        {
            foreach (var culture in new[] { "en-US", "zh-TW" })
            {
                L.Use(new LocalizationManager(culture));
                var body = DitherDialog.Body(DitherStyle.Scanlines, new DitherSettings());
                foreach (var key in new[] { "Dither.LineSpacing", "Dither.Glow", "Dither.Dots", "Dither.Wobble", "Dither.Density", "Dither.Contrast" })
                    Assert.True(Row(body, key).IsVisible, key);
                foreach (var key in new[] { "Dither.PixelSize", "Dither.PixelShape", "Dither.CellSize", "Dither.AngleDegrees", "Dither.Characters", "Dither.DiffusionPercent" })
                    Assert.False(Row(body, key).IsVisible, key);
                var boxes = body.GetLogicalDescendants().OfType<ComboBox>().ToArray();
                Assert.Contains(boxes[0].Items.OfType<ComboBoxItem>(), item => Equals(item.Content, L.Get("Dither.Style.Scanlines")));
                Assert.Equal(3, boxes[2].Items.Count);
                Assert.Equal(culture == "zh-TW" ? "掃描線（CRT）" : "Scanlines (CRT)", LocalizedNames.Dither(DitherStyle.Scanlines));
            }
        }
        finally { L.Use(was); }
    }

    [Fact]
    public async Task ScanlinesPreviewsSnapshotSettingsAndCancelKeepsTheRememberedAmounts()
    {
        DesktopTestHost.EnsureInitialized();
        using var context = new UiContext();
        var start = new DitherSettings { LineSpacing = 7, Glow = 25, Dots = 30, Wobble = 9 };
        var owner = new Owner();
        var previews = new List<DitherSettings>();
        var ask = DitherDialog.Ask(owner.Window, DitherStyle.Scanlines, start, (_, settings) => previews.Add(settings));
        var dialog = Assert.IsType<DitherDialog>(Assert.Single(owner.Window.OwnedWindows));
        Slider(dialog, "Dither.Glow").Value = 88;
        var first = Assert.IsType<DitherSettings>(previews.Last());
        Slider(dialog, "Dither.Glow").Value = 65;
        Assert.Equal(88, first.Glow);
        Assert.Equal(25, start.Glow);
        Button(dialog, "Common.Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        owner.Window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ask.IsCompletedSuccessfully);
        Assert.Null(await ask);
        Assert.Equal(25, start.Glow);
    }

    [Fact]
    public async Task ResetAndApplyReturnTheFourCrtDefaultsAndTheChosenStyle()
    {
        DesktopTestHost.EnsureInitialized();
        using var context = new UiContext();
        var owner = new Owner();
        var start = new DitherSettings { LineSpacing = 32, Glow = 99, Dots = 80, Wobble = 55 };
        var ask = DitherDialog.Ask(owner.Window, DitherStyle.Scanlines, start);
        var dialog = Assert.IsType<DitherDialog>(Assert.Single(owner.Window.OwnedWindows));
        Button(dialog, "Common.Reset").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var settings = dialog.Current();
        Assert.Equal(4, settings.LineSpacing); Assert.Equal(35, settings.Glow);
        Assert.Equal(0, settings.Dots); Assert.Equal(0, settings.Wobble);
        // Reset follows the existing Dither contract and resets the style to Atkinson too. Selecting CRT
        // again must expose those reset amounts, and Apply must return them through the ordinary dialog flow.
        var style = dialog.GetLogicalDescendants().OfType<ComboBox>().First();
        style.SelectedIndex = style.Items.Count - 1;
        Button(dialog, "Common.Apply").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        owner.Window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ask.IsCompletedSuccessfully);
        var result = await ask;
        Assert.True(result.HasValue);
        Assert.Equal(DitherStyle.Scanlines, result.Value.Style);
        Assert.Equal(35, result.Value.Settings.Glow);
        Assert.Equal(99, start.Glow);
    }

    [Theory]
    [InlineData("en-US", "Scanlines (CRT)", "Line Spacing", "Glow", "Dots", "Wobble")]
    [InlineData("zh-TW", "掃描線（CRT）", "掃描線間距", "光暈", "光點", "水平波動")]
    public void CrtResourcesHaveExactTranslations(string culture, params string[] expected)
    {
        var manager = new LocalizationManager(culture);
        var keys = new[] { "Dither.Style.Scanlines", "Dither.LineSpacing", "Dither.Glow", "Dither.Dots", "Dither.Wobble" };
        Assert.Equal(expected, keys.Select(key => manager.Get(key)));
    }

    private static Control Row(Control body, string key) => (Control)body.GetLogicalDescendants().OfType<TextBlock>()
        .First(text => text.Text == L.Get(key) && text.Parent is StackPanel).Parent!;

    private static Slider Slider(Control body, string key) => ((StackPanel)Row(body, key)).Children.OfType<Slider>().Single();
    private static Button Button(Control body, string key) => body.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, L.Get(key)));

    private sealed class Owner
    {
        public Window Window { get; } = new();
        public Owner() => Window.Show();
    }

    // The real app resumes dialog continuations on its UI dispatcher. Reproduce that context explicitly
    // and pump it synchronously, so xUnit cannot resume a headless UI fixture on another worker thread.
    private sealed class UiContext : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        public UiContext() => SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
