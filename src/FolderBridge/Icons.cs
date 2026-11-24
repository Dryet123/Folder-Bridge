using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderBridge;

// XP-style icons drawn as vectors on a 32x32 canvas, so they stay sharp at 16, 32 and 48 px.
static class Icons
{
    static readonly Rect Canvas = new(0, 0, 32, 32);

    static readonly Brush FoldBack = Vertical(("#F6D37C", 0), ("#D49B2E", 1));
    static readonly Brush FoldFront = Vertical(("#FFF1BE", 0), ("#F9D777", 0.45), ("#E7AF3F", 1));
    static readonly Brush ShadowFill = Radial(0.5, 0.5, 0.5, ("#52000000", 0), ("#00000000", 1));
    static readonly Brush Gloss = Vertical(("#F2FFFFFF", 0), ("#00FFFFFF", 1));
    static readonly Brush SoftGloss = Vertical(("#73FFFFFF", 0), ("#00FFFFFF", 1));
    static readonly Brush OrbGreen = Radial(0.5, 0.65, 0.62, ("#9BEA66", 0), ("#3FA827", 0.7), ("#23781A", 1));
    static readonly Brush OrbBlue = Radial(0.5, 0.65, 0.62, ("#86BCFF", 0), ("#2F6FE0", 0.7), ("#1745B0", 1));
    static readonly Brush OrbRed = Radial(0.5, 0.65, 0.62, ("#FF9C7C", 0), ("#E0442A", 0.7), ("#A8200F", 1));
    static readonly Brush OrbGray = Radial(0.5, 0.65, 0.62, ("#E8EBF0", 0), ("#9AA3B0", 0.7), ("#6B7480", 1));
    static readonly Brush Triangle = Vertical(("#FFF2A8", 0), ("#FFD22E", 0.5), ("#E9A500", 1));
    static readonly Brush Screen = Linear(new Point(0, 0), new Point(1, 1), ("#9AD6FF", 0), ("#3478DC", 0.55), ("#173F9E", 1));
    static readonly Brush Bezel = Vertical(("#F7F7F3", 0), ("#BFC4CC", 1));
    static readonly Brush ArrowGreen = Horizontal(("#A6EC86", 0), ("#4DBB2C", 0.5), ("#2C8A1A", 1));
    static readonly Brush ArrowBlue = Horizontal(("#A8CCFF", 0), ("#3C7BE6", 0.5), ("#1C4DB8", 1));
    static readonly Brush Metal = Linear(new Point(0, 0), new Point(1, 1), ("#F4F6FA", 0), ("#8E99AA", 1));
    static readonly Brush Bin = Horizontal(("#F3F7FC", 0), ("#C3D2E6", 0.6), ("#8FA6C6", 1));

    public static readonly DrawingImage Folder = Image(FolderParts());
    public static readonly DrawingImage Laptop = Image(
        Shadow(28.4, 14),
        Box(6, 3.5, 20, 16, 1.6, Bezel, "#5D6672"),
        Box(8.2, 5.7, 15.6, 11.6, 0, Screen, "#2C3E66", 0.7),
        Shape("M8.5,6 H17 L9.5,17 H8.5 Z", Solid("#47FFFFFF")),
        Shape("M4.5,20 H27.5 L30.6,25.6 Q31.2,27 29.6,27 H2.4 Q0.8,27 1.4,25.6 Z", Bezel, "#5D6672"),
        Shape("M6.5,22.2 H25.5 M5.8,24.3 H26.2", null, "#9AA3B0", 1));
    public static readonly DrawingImage Monitor = Image(MonitorParts());
    public static readonly DrawingImage Ok = Image(Orb(OrbGreen, "#23701A", Embossed("M9.6,17 L14.2,21.6 L22.8,11.8", "#731E5A12")));
    public static readonly DrawingImage Add = Image(Orb(OrbGreen, "#23701A", Embossed("M16,9.5 V23.5 M9,16.5 H23", "#731E5A12")));
    public static readonly DrawingImage Error = Image(Orb(OrbRed, "#8A1A0A", Embossed("M11.5,12 L20.5,21 M20.5,12 L11.5,21", "#736A1408")));
    public static readonly DrawingImage Offline = Image(Orb(OrbGray, "#5F6875", Embossed("M10.5,16.5 H21.5", "#734A525E")));
    public static readonly DrawingImage Paused = Image(Orb(OrbGray, "#5F6875", [Shape("M11.5,10.5 H14.5 V22.5 H11.5 Z M17.5,10.5 H20.5 V22.5 H17.5 Z", Brushes.White)]));
    public static readonly DrawingImage Sync = Image(SyncParts());
    public static readonly DrawingImage Warn = Image(
        Shadow(29.2, 13),
        Shape("M16,3 Q17.3,3 18,4.3 L29.8,25.8 Q30.5,27.6 28.6,27.6 H3.4 Q1.5,27.6 2.2,25.8 L14,4.3 Q14.7,3 16,3 Z", Triangle, "#A97500"),
        Shape("M16,6 L25.5,23.5 H6.5 Z", SoftGloss),
        Box(14.3, 10, 3.4, 10.5, 1.7, Solid("#262626")),
        Ellipse(16, 23.6, 1.9, 1.9, Solid("#262626")));
    public static readonly DrawingImage Down = Image(
        Shadow(29.3, 9),
        Shape("M12,3.5 H20 Q21,3.5 21,4.5 V14.5 H25.6 Q27.3,14.5 26.2,15.8 L16.9,26.6 Q16,27.6 15.1,26.6 L5.8,15.8 Q4.7,14.5 6.4,14.5 H11 V4.5 Q11,3.5 12,3.5 Z", ArrowGreen, "#2A7316"),
        Shape("M12.7,5 V15.6", null, "#B3FFFFFF"));
    public static readonly DrawingImage Up = Image(
        Shadow(29.6, 7),
        Shape("M12,28.5 H20 Q21,28.5 21,27.5 V17.5 H25.6 Q27.3,17.5 26.2,16.2 L16.9,5.4 Q16,4.4 15.1,5.4 L5.8,16.2 Q4.7,17.5 6.4,17.5 H11 V27.5 Q11,28.5 12,28.5 Z", ArrowBlue, "#1A47A8"),
        Shape("M12.7,27 V16.4", null, "#B3FFFFFF"));
    public static readonly DrawingImage Pair = Image(
        Group(new ScaleTransform(0.62, 0.62), MonitorParts()),
        Group(new TransformGroup { Children = { new ScaleTransform(0.62, 0.62), new TranslateTransform(12, 11) } }, MonitorParts()),
        Shape("M9.9,17.4 V23.2 H13.4", null, "#2F6FE0", 1.6));
    public static readonly DrawingImage Gear = Image(
        Shadow(29.4, 11),
        Shape("M25.72,13.17 L29.08,13.74 L29.08,17.26 L25.72,17.83 L24.53,20.72 L26.49,23.51 L24.01,25.99 L21.22,24.03 L18.33,25.22 L17.76,28.58 L14.24,28.58 L13.67,25.22 L10.78,24.03 L7.99,25.99 L5.51,23.51 L7.47,20.72 L6.28,17.83 L2.92,17.26 L2.92,13.74 L6.28,13.17 L7.47,10.28 L5.51,7.49 L7.99,5.01 L10.78,6.97 L13.67,5.78 L14.24,2.42 L17.76,2.42 L18.33,5.78 L21.22,6.97 L24.01,5.01 L26.49,7.49 L24.53,10.28 Z", Metal, "#5F6977", round: true),
        Ellipse(16, 15.5, 4.3, 4.3, Solid("#F4F6F9"), "#5F6977"));
    public static readonly DrawingImage Pause = Image(
        Shadow(29.2, 10),
        Box(7, 4, 7, 23, 1.8, ArrowBlue, "#1A47A8"),
        Box(18, 4, 7, 23, 1.8, ArrowBlue, "#1A47A8"),
        Shape("M9.2,6 V25 M20.2,6 V25", null, "#99FFFFFF"));
    public static readonly DrawingImage Play = Image(
        Shadow(29.4, 10),
        Shape("M9,5 Q9,3.2 10.6,4.1 L26.2,14.6 Q27.6,15.6 26.2,16.6 L10.6,27.1 Q9,28 9,26.2 Z", Vertical(("#A6EC86", 0), ("#4DBB2C", 0.5), ("#2C8A1A", 1)), "#2A7316"),
        Shape("M11,7 L22,14.5", null, "#B3FFFFFF"));
    public static readonly DrawingImage Trash = Image(
        Shadow(29.4, 9),
        Shape("M7,9 H25 L23.3,27.6 Q23.2,28.8 22,28.8 H10 Q8.8,28.8 8.7,27.6 Z", Bin, "#53698A"),
        Shape("M11.5,12 L12.2,25.8 M16,12 V25.8 M20.5,12 L19.8,25.8", null, "#7D93B3", 1.1),
        Box(5, 5.8, 22, 3.6, 1.4, Bin, "#53698A"),
        Shape("M13,5.8 V4 Q13,3.2 13.8,3.2 H18.2 Q19,3.2 19,4 V5.8", null, "#53698A"));
    public static readonly DrawingImage App = Image(
        Group(null, FolderParts()),
        Group(new TransformGroup { Children = { new ScaleTransform(0.6, 0.6), new TranslateTransform(13.5, 12.5) } }, SyncParts()));

    public static void Register(ResourceDictionary resources)
    {
        foreach (var field in typeof(Icons).GetFields())
            if (field.GetValue(null) is DrawingImage image)
                resources["Icon." + field.Name] = image;
    }

    static Drawing[] FolderParts() =>
    [
        Shadow(28.2, 13.5),
        Shape("M2.5,7 Q2.5,5 4.5,5 H11 Q12,5 12.7,5.8 L14,7.5 H26.5 Q28.5,7.5 28.5,9.5 V25 Q28.5,27 26.5,27 H4.5 Q2.5,27 2.5,25 Z", FoldBack, "#A8761A"),
        Box(6, 8.5, 19, 8, 0, Brushes.White, "#CFC6A6", 0.8),
        Shape("M8.5,11 H22 M8.5,13.5 H19", null, "#C9D3E3", 1),
        Shape("M5,12.2 Q5.3,10.5 7,10.5 H29.4 Q31.2,10.5 30.8,12.2 L28.3,25.4 Q28,27 26.3,27 H4.4 Q2.6,27 2.9,25.3 Z", FoldFront, "#A8761A"),
        Shape("M7,11.9 H29.1", null, "#D9FFFFFF", 1),
    ];

    static Drawing[] MonitorParts() =>
    [
        Shadow(28.6, 11),
        Shape("M12.5,21.5 H19.5 L21,25 H11 Z", Solid("#C3C9D2"), "#5D6672", 1),
        Box(7.5, 24.5, 17, 3, 1.2, Bezel, "#5D6672", 1),
        Box(2.5, 3, 27, 19, 2, Bezel, "#5D6672"),
        Box(5, 5.5, 22, 13.5, 0, Screen, "#2C3E66", 0.7),
        Shape("M5.3,5.8 H16 L7.5,18.7 H5.3 Z", Solid("#47FFFFFF")),
        Box(24, 19.9, 2.6, 1, 0, Solid("#3FBF2A")),
    ];

    static Drawing[] SyncParts() => Orb(OrbBlue, "#163F9E",
    [
        Shape("M22.6,14.1 A7,7 0 0 0 9.4,14.1 M9.4,18.9 A7,7 0 0 0 22.6,18.9", null, "#FFFFFFFF", 2.6),
        Shape("M6,13.2 H12.8 L9.4,17.8 Z M19.2,19.8 H26 L22.6,15.2 Z", Brushes.White),
    ]);

    static Drawing[] Orb(Brush fill, string stroke, Drawing[] glyph) =>
    [
        Shadow(29.6, 11),
        Ellipse(16, 16.5, 13, 13, fill, stroke),
        Ellipse(16, 10.3, 9.5, 6.2, Gloss),
        .. glyph,
    ];

    static Drawing[] Embossed(string data, string shadow) =>
    [
        Group(new TranslateTransform(0, 0.9), Shape(data, null, shadow, 4.4, true)),
        Shape(data, null, "#FFFFFFFF", 3.3, true),
    ];

    static Drawing Shadow(double cy, double rx) => Ellipse(16, cy, rx, 1.8, ShadowFill);

    static Drawing Shape(string data, Brush? fill, string? stroke = null, double width = 1.2, bool round = false) =>
        Frozen(new GeometryDrawing(fill, MakePen(stroke, width, round), Geometry.Parse(data)));

    static Drawing Box(double x, double y, double w, double h, double r, Brush fill, string? stroke = null, double width = 1.2) =>
        Frozen(new GeometryDrawing(fill, MakePen(stroke, width, false), new RectangleGeometry(new Rect(x, y, w, h), r, r)));

    static Drawing Ellipse(double cx, double cy, double rx, double ry, Brush fill, string? stroke = null) =>
        Frozen(new GeometryDrawing(fill, MakePen(stroke, 1.2, false), new EllipseGeometry(new Point(cx, cy), rx, ry)));

    static Drawing Group(Transform? transform, params Drawing[] parts)
    {
        var g = new DrawingGroup { Transform = transform };
        foreach (var p in parts)
            g.Children.Add(p);
        return Frozen(g);
    }

    static Pen? MakePen(string? color, double width, bool round)
    {
        if (color == null)
            return null;
        var cap = round ? PenLineCap.Round : PenLineCap.Flat;
        return Frozen(new Pen(Solid(color), width)
        {
            LineJoin = round ? PenLineJoin.Round : PenLineJoin.Miter,
            StartLineCap = cap,
            EndLineCap = cap,
        });
    }

    static DrawingImage Image(params Drawing[] parts)
    {
        var g = new DrawingGroup { ClipGeometry = new RectangleGeometry(Canvas) };
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(Canvas)));
        foreach (var p in parts)
            g.Children.Add(p);
        return Frozen(new DrawingImage(g));
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Brush Solid(string hex) => Frozen(new SolidColorBrush(Hex(hex)));
    static Brush Vertical(params (string Color, double Offset)[] stops) => Linear(new Point(0, 0), new Point(0, 1), stops);
    static Brush Horizontal(params (string Color, double Offset)[] stops) => Linear(new Point(0, 0), new Point(1, 0), stops);

    static Brush Linear(Point start, Point end, params (string Color, double Offset)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(Hex(c), o));
        return Frozen(b);
    }

    static Brush Radial(double cx, double cy, double r, params (string Color, double Offset)[] stops)
    {
        var b = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = r, RadiusY = r };
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(Hex(c), o));
        return Frozen(b);
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public static BitmapSource Render(ImageSource image, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // Builds a .ico with PNG frames, which Windows accepts at every size.
    public static byte[] IcoFile(ImageSource image, params int[] sizes)
    {
        var frames = sizes.Select(s => Png(Render(image, s))).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            var dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames)
            w.Write(f);
        w.Flush();
        return ms.ToArray();
    }
}
