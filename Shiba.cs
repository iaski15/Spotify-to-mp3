using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyToMp3;

public enum ShibaMood { Neutral, Happy, Sad, Sleepy }

/// <summary>
/// The Shibaberg mascot, ported from Autoberg's Ui.cs (GDI+) to WPF: a chibi shiba head drawn as vectors
/// in a 100x100 design space, scaled to whatever box it's given.
/// </summary>
public sealed class ShibaFace : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(ShibaMood), typeof(ShibaFace),
        new FrameworkPropertyMetadata(ShibaMood.Neutral, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShibaMood Mood
    {
        get => (ShibaMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    private static readonly Brush Fur = Hex("#E8924A"), Cream = Hex("#FFF2DF"), Ink = Hex("#2A1A12"),
        Blush = Hex("#96FF8FA3"), Tongue = Hex("#FF7C8C"), Zzz = Hex("#C4652A");

    private static readonly Pen Line = MakeLine();

    protected override void OnRender(DrawingContext dc) => Draw(dc, Math.Min(ActualWidth, ActualHeight), Mood,
        (ActualWidth - Math.Min(ActualWidth, ActualHeight)) / 2, (ActualHeight - Math.Min(ActualWidth, ActualHeight)) / 2,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Window icon / anywhere a bitmap is needed.</summary>
    public static BitmapSource Render(int size, ShibaMood mood)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) Draw(dc, size, mood, 0, 0, 1);
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    private static void Draw(DrawingContext dc, double size, ShibaMood mood, double x, double y, double pixelsPerDip)
    {
        if (size <= 0) return;
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(size / 100, size / 100));

        // ears (behind the head)
        foreach (var left in new[] { true, false })
        {
            dc.DrawGeometry(Fur, Line, Poly(Mirror(left, 13, 44, 19, 9, 45, 27)));
            dc.DrawGeometry(Cream, null, Poly(Mirror(left, 20, 37, 23, 18, 36, 29)));
        }

        // head: wide chibi oval, cream muzzle clipped to it, the two "eyebrow" spots
        var head = new EllipseGeometry(new Rect(7, 22, 86, 70));
        dc.DrawGeometry(Fur, null, head);
        dc.PushClip(head);
        dc.DrawGeometry(Cream, null, Oval(17, 54, 66, 37));
        dc.Pop();
        dc.DrawGeometry(Cream, null, Oval(28, 41, 10, 6));
        dc.DrawGeometry(Cream, null, Oval(62, 41, 10, 6));
        dc.DrawGeometry(null, Line, head);

        dc.DrawGeometry(Blush, null, Oval(16, 63, 13, 7));
        dc.DrawGeometry(Blush, null, Oval(71, 63, 13, 7));

        // eyes
        switch (mood)
        {
            case ShibaMood.Happy:
                dc.DrawGeometry(null, Line, Arc(27, 51, 12, 10, 200, 140));
                dc.DrawGeometry(null, Line, Arc(61, 51, 12, 10, 200, 140));
                break;
            case ShibaMood.Sleepy:
                dc.DrawGeometry(null, Line, Arc(27, 50, 12, 9, 20, 140));
                dc.DrawGeometry(null, Line, Arc(61, 50, 12, 9, 20, 140));
                break;
            default:
                dc.DrawGeometry(Ink, null, Oval(29, 50, 9, 11));
                dc.DrawGeometry(Ink, null, Oval(62, 50, 9, 11));
                dc.DrawGeometry(Brushes.White, null, Oval(31.5, 52, 3.5, 3.5));
                dc.DrawGeometry(Brushes.White, null, Oval(64.5, 52, 3.5, 3.5));
                if (mood == ShibaMood.Sad)
                {
                    dc.DrawLine(Line, new Point(26, 45), new Point(37, 42)); // worried brows
                    dc.DrawLine(Line, new Point(74, 45), new Point(63, 42));
                }
                break;
        }

        // nose + mouth
        var nosePen = new Pen(Ink, 2.5) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(Ink, nosePen, Poly([new(45.5, 63.5), new(54.5, 63.5), new(50, 68)]));
        switch (mood)
        {
            case ShibaMood.Happy:
                dc.DrawGeometry(Tongue, Line, Arc(42, 64, 16, 16, 0, 180, closed: true));
                break;
            case ShibaMood.Sad:
                dc.DrawGeometry(null, Line, Arc(43, 72, 14, 9, 200, 140));
                break;
            default:
                dc.DrawGeometry(null, Line, Arc(41, 64, 9, 8, 0, 180)); // the little "w"
                dc.DrawGeometry(null, Line, Arc(50, 64, 9, 8, 0, 180));
                break;
        }
        if (mood == ShibaMood.Sleepy)
            dc.DrawText(new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                15, Zzz, pixelsPerDip), new Point(82, 10));

        dc.Pop();
        dc.Pop();
    }

    private static Point[] Mirror(bool left, params double[] xy) =>
        Enumerable.Range(0, xy.Length / 2).Select(i => new Point(left ? xy[i * 2] : 100 - xy[i * 2], xy[i * 2 + 1])).ToArray();

    private static Geometry Poly(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], isFilled: true, isClosed: true);
            c.PolyLineTo(pts[1..], isStroked: true, isSmoothJoin: true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Oval(double x, double y, double w, double h) => new EllipseGeometry(new Rect(x, y, w, h));

    // GDI+ DrawArc semantics: angles in degrees, clockwise from +X, on the ellipse inscribed in (x, y, w, h).
    private static Geometry Arc(double x, double y, double w, double h, double start, double sweep, bool closed = false)
    {
        double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
        Point At(double deg) => new(cx + rx * Math.Cos(deg * Math.PI / 180), cy + ry * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = At(start), IsClosed = closed, IsFilled = closed };
        fig.Segments.Add(new ArcSegment(At(start + sweep), new Size(rx, ry), 0, Math.Abs(sweep) > 180,
            sweep > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
        return new PathGeometry([fig]);
    }

    private static Brush Hex(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static Pen MakeLine()
    {
        var p = new Pen(Hex("#3A2418"), 3.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}

/// <summary>Soft paw-print pattern for the sidebar, same spots as Shibaberg's (bottom ones anchored to the bottom).</summary>
public sealed class PawPrints : FrameworkElement
{
    private static readonly Brush Paw = new SolidColorBrush(Color.FromArgb(90, 0xD2, 0x7A, 0x34));

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        foreach (var (x, y) in new[] { (48.0, 30.0), (230.0, 120.0), (30.0, h - 140), (70.0, h - 180), (200.0, h - 80), (236.0, h - 125) })
        {
            dc.DrawEllipse(Paw, null, new Point(x, y + 1), 9, 7);
            dc.DrawEllipse(Paw, null, new Point(x - 9.5, y - 11), 3.5, 4);
            dc.DrawEllipse(Paw, null, new Point(x - 0.5, y - 16), 3.5, 4);
            dc.DrawEllipse(Paw, null, new Point(x + 8.5, y - 11), 3.5, 4);
        }
    }
}
