using System.Windows;
using System.Windows.Media;

namespace CutePdf
{
    // The built-in mascot: a fluffy cloud with a happy face. Shown until real pictures
    // are added, and also used to draw the app icon (see tools/IconGen.cs).
    public static class CloudArt
    {
        static readonly Color Ink = Color.FromRgb(0x3E, 0x4C, 0x6D);
        static readonly Color Outline = Color.FromRgb(0xA6, 0xD6, 0xF3);
        static readonly Color Shade = Color.FromRgb(0xE8, 0xF4, 0xFD);
        static readonly Color Blush = Color.FromRgb(0xFF, 0xB3, 0xC8);

        // Drawn in a 220 x 170 box. "simple" drops the face for tiny icon sizes.
        public static Drawing CreateCloud(bool simple)
        {
            Geometry body = new EllipseGeometry(new Point(62, 98), 44, 40);
            body = Union(body, new EllipseGeometry(new Point(108, 68), 52, 50));
            body = Union(body, new EllipseGeometry(new Point(160, 94), 44, 42));
            body = Union(body, new EllipseGeometry(new Point(110, 118), 84, 40));

            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.White, null, body));
            var shade = new DrawingGroup();
            shade.ClipGeometry = body;
            shade.Children.Add(new GeometryDrawing(new SolidColorBrush(Shade), null, new EllipseGeometry(new Point(110, 162), 96, 26)));
            g.Children.Add(shade);
            var outline = new Pen(new SolidColorBrush(Outline), simple ? 9 : 5);
            outline.LineJoin = PenLineJoin.Round;
            g.Children.Add(new GeometryDrawing(null, outline, body));

            if (!simple)
            {
                var ink = new SolidColorBrush(Ink);
                g.Children.Add(new GeometryDrawing(ink, null, new EllipseGeometry(new Point(93, 103), 6.5, 8.5)));
                g.Children.Add(new GeometryDrawing(ink, null, new EllipseGeometry(new Point(127, 103), 6.5, 8.5)));
                g.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(95.2, 99.6), 2.4, 2.4)));
                g.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(129.2, 99.6), 2.4, 2.4)));
                var blush = new SolidColorBrush(Blush);
                blush.Opacity = 0.85;
                g.Children.Add(new GeometryDrawing(blush, null, new EllipseGeometry(new Point(75, 119), 11, 6.5)));
                g.Children.Add(new GeometryDrawing(blush, null, new EllipseGeometry(new Point(145, 119), 11, 6.5)));
                var mouthPen = new Pen(ink, 2.6);
                mouthPen.StartLineCap = PenLineCap.Round;
                mouthPen.EndLineCap = PenLineCap.Round;
                mouthPen.LineJoin = PenLineJoin.Round;
                g.Children.Add(new GeometryDrawing(null, mouthPen, Geometry.Parse("M101,114 Q105.5,120 110,114 Q114.5,120 119,114")));
                g.Children.Add(Sparkle(new Point(198, 28), 10, Color.FromRgb(0xFF, 0xA3, 0xBE)));
                g.Children.Add(Sparkle(new Point(22, 44), 7, Color.FromRgb(0x9F, 0xD6, 0xF5)));
            }
            g.Freeze();
            return g;
        }

        // 256 x 256 app icon: the cloud on a sky-blue rounded square.
        public static Drawing CreateIcon(bool simple)
        {
            var g = new DrawingGroup();
            var sky = new LinearGradientBrush(Color.FromRgb(0xB8, 0xE4, 0xFA), Color.FromRgb(0x72, 0xBD, 0xEA), 90);
            g.Children.Add(new GeometryDrawing(sky, null, new RectangleGeometry(new Rect(0, 0, 256, 256), 58, 58)));
            var cloud = new DrawingGroup();
            cloud.Transform = new MatrixTransform(1.15, 0, 0, 1.15, 0.4, 32);
            cloud.Children.Add(CreateCloud(simple));
            g.Children.Add(cloud);
            g.Freeze();
            return g;
        }

        public static ImageSource CreateImage()
        {
            var image = new DrawingImage(CreateCloud(false));
            image.Freeze();
            return image;
        }

        static Drawing Sparkle(Point c, double r, Color color)
        {
            double k = r * 0.18;
            var figure = new PathFigure();
            figure.StartPoint = new Point(c.X, c.Y - r);
            figure.IsClosed = true;
            figure.Segments.Add(new QuadraticBezierSegment(new Point(c.X + k, c.Y - k), new Point(c.X + r, c.Y), true));
            figure.Segments.Add(new QuadraticBezierSegment(new Point(c.X + k, c.Y + k), new Point(c.X, c.Y + r), true));
            figure.Segments.Add(new QuadraticBezierSegment(new Point(c.X - k, c.Y + k), new Point(c.X - r, c.Y), true));
            figure.Segments.Add(new QuadraticBezierSegment(new Point(c.X - k, c.Y - k), new Point(c.X, c.Y - r), true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new GeometryDrawing(new SolidColorBrush(color), null, geometry);
        }

        static Geometry Union(Geometry a, Geometry b)
        {
            return Geometry.Combine(a, b, GeometryCombineMode.Union, null);
        }
    }
}
