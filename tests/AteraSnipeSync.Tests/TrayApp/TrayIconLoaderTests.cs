using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies the bundled TrayApp icon contract without showing UI or reading any external resource.
/// </summary>
public sealed class TrayIconLoaderTests
{
    [Fact]
    public void BundledResource_ContainsExpectedMultiResolutionFrames()
    {
        var assembly = typeof(TrayIconLoader).Assembly;
        var iconResources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith("tray-icon.ico", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([TrayIconLoader.ResourceName], iconResources);

        using var stream = assembly.GetManifestResourceStream(TrayIconLoader.ResourceName);
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(8, reader.ReadUInt16());

        var sizes = new List<int>();
        for (var index = 0; index < 8; index++)
        {
            var widthByte = reader.ReadByte();
            var heightByte = reader.ReadByte();
            var width = widthByte == 0 ? 256 : widthByte;
            var height = heightByte == 0 ? 256 : heightByte;

            Assert.Equal(width, height);
            sizes.Add(width);
            Assert.Equal(14, reader.ReadBytes(14).Length);
        }

        Assert.Equal([16, 20, 24, 32, 48, 64, 128, 256], sizes.Order());
    }

    [Fact]
    public void Load_ReturnsUsableTransparentBlueIcon()
    {
        using var icon = TrayIconLoader.Load();
        using var bitmap = icon.ToBitmap();

        Assert.True(icon.Width >= 16);
        Assert.Equal(icon.Width, icon.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);

        var hasOpaqueBluePixel = false;
        for (var y = 0; y < bitmap.Height && !hasOpaqueBluePixel; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 200 && pixel.B >= 180 && pixel.B >= pixel.R + 80)
                {
                    hasOpaqueBluePixel = true;
                    break;
                }
            }
        }

        Assert.True(hasOpaqueBluePixel);
    }
}
