using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkillSync.Daemon.Desktop;

public static class SkillHubIconRenderer
{
    private static readonly int[] StandardIconSizes = [16, 32, 48, 64, 256];

    /// <summary>
    /// Creates a multi-resolution native Windows Icon containing the SkillHub hub mark.
    /// </summary>
    public static Icon CreateAppIcon(int preferredSize = 64)
    {
        try
        {
            using var ms = new MemoryStream();
            WriteMultiResolutionIcoToStream(ms);
            ms.Position = 0;
            return new Icon(ms, new Size(preferredSize, preferredSize));
        }
        catch
        {
            return CreateHubIcon(preferredSize);
        }
    }

    /// <summary>
    /// Renders the SkillHub hub mark at the requested pixel size and returns a native Windows Icon.
    /// </summary>
    public static Icon CreateHubIcon(int size = 64)
    {
        using var bmp = RenderHubBitmap(size);
        var hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Writes a multi-resolution .ico file (16x16, 32x32, 48x48, 64x64, 256x256 PNG-encoded frames)
    /// to disk for embedding in the .exe and for Desktop/Start Menu shortcuts.
    /// </summary>
    public static void SaveIconFile(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteMultiResolutionIcoToStream(fs);
        }
        catch
        {
        }
    }

    private static void WriteMultiResolutionIcoToStream(Stream output)
    {
        var frames = new List<(int Size, byte[] PngBytes)>(StandardIconSizes.Length);
        foreach (var sz in StandardIconSizes)
        {
            using var bmp = RenderHubBitmap(sz);
            using var pngStream = new MemoryStream();
            bmp.Save(pngStream, ImageFormat.Png);
            frames.Add((sz, pngStream.ToArray()));
        }

        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICONDIR header (6 bytes)
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type: 1 = Icon
        bw.Write((ushort)frames.Count); // Count of images

        var offset = 6 + (16 * frames.Count);
        foreach (var (sz, pngBytes) in frames)
        {
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width (0 means 256)
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height (0 means 256)
            bw.Write((byte)0);                    // ColorCount
            bw.Write((byte)0);                    // Reserved
            bw.Write((ushort)1);                  // Planes
            bw.Write((ushort)32);                 // BitCount
            bw.Write((uint)pngBytes.Length);      // BytesInRes
            bw.Write((uint)offset);               // ImageOffset
            offset += pngBytes.Length;
        }

        foreach (var (_, pngBytes) in frames)
        {
            bw.Write(pngBytes);
        }
    }

    /// <summary>Draws <see cref="SkillHubBrand"/> (the same geometry as <c>skillhub-logo.svg</c>) at the requested pixel size.</summary>
    public static Bitmap RenderHubBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var scale = size / SkillHubBrand.ViewBox;
        float S(float value) => value * scale;
        var inset = S(SkillHubBrand.TileInset);
        var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

        using (var path = CreateRoundedRectPath(rect, S(SkillHubBrand.TileRadius)))
        using (var tile = new LinearGradientBrush(rect, ColorTranslator.FromHtml(SkillHubBrand.GradientStart),
            ColorTranslator.FromHtml(SkillHubBrand.GradientEnd), LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(tile, path);
        }

        var center = S(SkillHubBrand.Center);
        var orbit = S(SkillHubBrand.OrbitRadius);
        using (var ring = new Pen(Color.FromArgb((int)MathF.Round(255 * SkillHubBrand.OrbitOpacity), 255, 255, 255), S(SkillHubBrand.OrbitStroke)))
        {
            g.DrawEllipse(ring, center - orbit, center - orbit, orbit * 2, orbit * 2);
        }

        using var white = new SolidBrush(Color.White);
        using (var spoke = new Pen(Color.White, S(SkillHubBrand.SpokeStroke)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            for (var index = 0; index < SkillHubBrand.NodeAngles.Length; index++)
            {
                var (x, y) = SkillHubBrand.Node(index);
                g.DrawLine(spoke, center, center, S(x), S(y));
            }
        }

        void Dot(float x, float y, float radius) => g.FillEllipse(white, S(x - radius), S(y - radius), S(radius * 2), S(radius * 2));
        Dot(SkillHubBrand.Center, SkillHubBrand.Center, SkillHubBrand.HubRadius);
        for (var index = 0; index < SkillHubBrand.NodeAngles.Length; index++)
        {
            var (x, y) = SkillHubBrand.Node(index);
            Dot(x, y, SkillHubBrand.NodeRadius);
        }

        return bmp;
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
