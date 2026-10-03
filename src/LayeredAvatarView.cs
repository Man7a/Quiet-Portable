using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietGPT;

// Draw the user's original PNGs without resampling or modifying the source files.
public sealed class LayeredAvatarView : FrameworkElement
{
    private readonly BitmapImage face = Load("face.png");
    private readonly BitmapImage whites = Load("whites.png");
    private readonly BitmapImage irises = Load("eyes.png");
    private double gazeX, gazeY;
    private Vector rightGaze;
    public Vector Gaze => new(gazeX, gazeY);
    public Vector RightGaze => rightGaze;
    private static BitmapImage Load(string name)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/QuietGPT;component/Assets/CompanionEyes/" + name);
        image.EndInit(); image.Freeze(); return image;
    }

    public void SetGaze(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        double length = Math.Sqrt(x * x + y * y);
        if (length > 1) { x /= length; y /= length; }
        gazeX = x; gazeY = y; rightGaze = new Vector(x, y); InvalidateVisual();
    }

    public void AimAtLocalPoint(Point target, double blend = 0.18)
    {
        double scale = Math.Min(ActualWidth / 1254, ActualHeight / 1254);
        if (scale <= 0 || !double.IsFinite(target.X) || !double.IsFinite(target.Y)) return;
        var point = new Point((target.X - (ActualWidth - 1254 * scale) / 2) / scale,
            (target.Y - (ActualHeight - 1254 * scale) / 2) / scale);
        Vector Aim(double eyeX)
        {
            double x = point.X - eyeX, y = point.Y - 402;
            // A virtual target depth softens convergence instead of snapping toward the nose.
            double distance = Math.Sqrt(x * x + y * y + 100 * 100);
            return new Vector(x / distance, y / distance);
        }
        Vector left = Aim(551), right = Aim(717);
        blend = Math.Clamp(blend, 0, 1);
        Vector current = Gaze;
        if ((left - current).Length + (right - rightGaze).Length < 0.002) return;
        current += (left - current) * blend;
        rightGaze += (right - rightGaze) * blend;
        gazeX = current.X; gazeY = current.Y; InvalidateVisual();
    }

    public Point EyeAnchor
    {
        get
        {
            double scale = Math.Min(ActualWidth / 1254, ActualHeight / 1254);
            return new Point((ActualWidth - 1254 * scale) / 2 + 634 * scale,
                (ActualHeight - 1254 * scale) / 2 + 402 * scale);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double scale = Math.Min(ActualWidth / 1254, ActualHeight / 1254);
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 1254 * scale) / 2, (ActualHeight - 1254 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var eyeRegions = new GeometryGroup();
        eyeRegions.Children.Add(new RectangleGeometry(new Rect(502, 379, 91, 53)));
        eyeRegions.Children.Add(new RectangleGeometry(new Rect(675, 379, 93, 53)));
        dc.PushClip(eyeRegions);
        dc.DrawImage(whites, new Rect(0, 0, 1254, 1254));
        dc.Pop();
        // Isolate each eye at draw time; the original PNG is kept intact.
        dc.PushClip(new RectangleGeometry(new Rect(502, 379, 91, 53)));
        dc.DrawImage(irises, new Rect(gazeX * 9, gazeY * 6, 1254, 1254));
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(675, 379, 93, 53)));
        dc.DrawImage(irises, new Rect(rightGaze.X * 9, rightGaze.Y * 6, 1254, 1254));
        dc.Pop();
        dc.DrawImage(face, new Rect(0, 0, 1254, 1254));
        dc.Pop(); dc.Pop();
    }
}
