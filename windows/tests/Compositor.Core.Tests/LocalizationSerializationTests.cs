using System.Globalization;
using System.Text;
using Compositor.Core.IO;

namespace Compositor.Core.Tests;

/// <summary>Localization choices must never change the cross-platform .comp format.</summary>
public class LocalizationSerializationTests : ProjectTestBase
{
    [Fact]
    public void TraditionalChineseCultureDoesNotChangeProjectManifestSerialization()
    {
        using var snapshot = KitchenSink();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var english = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentCulture = english;
            CultureInfo.CurrentUICulture = english;
            var expected = Serialized(snapshot.Manifest);

            var traditionalChinese = CultureInfo.GetCultureInfo("zh-TW");
            CultureInfo.CurrentCulture = traditionalChinese;
            CultureInfo.CurrentUICulture = traditionalChinese;

            Assert.Equal(expected, Serialized(snapshot.Manifest));

            var package = PathIn("TraditionalChinese.comp");
            ProjectStore.Save(snapshot, package);
            Assert.Equal(expected, File.ReadAllText(Path.Combine(package, ProjectStore.ManifestName), Encoding.UTF8));

            using var loaded = ProjectStore.Load(package);
            Assert.Equal(expected, Serialized(loaded.Manifest));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
