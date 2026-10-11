using Avalonia;
using Avalonia.Headless;

namespace Compositor.Desktop.Tests;

/// <summary>One Avalonia Headless setup for the whole test assembly; Avalonia permits setup only once.</summary>
internal static class DesktopTestHost
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            AppBuilder.Configure<DesktopApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            _initialized = true;
        }
    }
}
