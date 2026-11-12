using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkillSync.Daemon.Desktop;

public static class SkillHubIconRenderer
{
    private static readonly int[] StandardIconSizes = [16, 32, 48, 64, 256];

    /// <summary>
    /// Creates a multi-resolution native Windows Icon containing the SkillHub isometric layered prism emblem.
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
    /// Renders the custom SkillHub isometric layered prism emblem at the requested pixel size
    /// and returns a native Windows Icon.
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

    public static Bitmap RenderHubBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var pad = size * 0.04f;
        var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        var radius = size * 0.24f;

        // 1. Rich Indigo -> Violet Squircle Background
        using (var path = CreateRoundedRectPath(rect, radius))
        using (var bgBrush = new LinearGradientBrush(
            rect,
            Color.FromArgb(255, 67, 56, 202),  // #4338CA Deep Indigo
            Color.FromArgb(255, 109, 40, 217), // #6D28D9 Rich Violet
            LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(bgBrush, path);
        }

        var strokeWidth = Math.Max(1.5f, size * 0.068f);

        // 2. Bottom & Middle Isometric Stack Chevrons (multi-provider skill layers)
        using (var bottomPen = new Pen(Color.FromArgb(185, 199, 210, 254), strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            var bottomChevron = new PointF[]
            {
                new(size * 0.21f, size * 0.67f),
                new(size * 0.50f, size * 0.81f),
                new(size * 0.79f, size * 0.67f)
            };
            g.DrawLines(bottomPen, bottomChevron);
        }

        using (var midPen = new Pen(Color.FromArgb(230, 224, 231, 255), strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            var midChevron = new PointF[]
            {
                new(size * 0.21f, size * 0.52f),
                new(size * 0.50f, size * 0.66f),
                new(size * 0.79f, size * 0.52f)
            };
            g.DrawLines(midPen, midChevron);
        }

        // 3. Top Isometric Diamond Plane (Filled Crisp White)
        var topDiamond = new PointF[]
        {
            new(size * 0.50f, size * 0.20f),
            new(size * 0.79f, size * 0.355f),
            new(size * 0.50f, size * 0.51f),
            new(size * 0.21f, size * 0.355f)
        };
        using (var topBrush = new SolidBrush(Color.White))
        using (var topPen = new Pen(Color.White, Math.Max(1.0f, size * 0.03f)) { LineJoin = LineJoin.Round })
        {
            g.FillPolygon(topBrush, topDiamond);
            g.DrawPolygon(topPen, topDiamond);
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
