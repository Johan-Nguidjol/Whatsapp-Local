using System;
using System.Windows;
using System.Windows.Media;

namespace ChatLocal
{
    /// <summary>Anneau segmenté autour d'un avatar : un arc par statut (vert = non vu, gris = vu).</summary>
    public class StatusRing : FrameworkElement
    {
        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
            nameof(Total), typeof(int), typeof(StatusRing),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SeenProperty = DependencyProperty.Register(
            nameof(Seen), typeof(int), typeof(StatusRing),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
            nameof(RingThickness), typeof(double), typeof(StatusRing),
            new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SeenBrushProperty = DependencyProperty.Register(
            nameof(SeenBrush), typeof(Brush), typeof(StatusRing),
            new FrameworkPropertyMetadata(MakeBrush(0xC4, 0xC9, 0xCC), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty UnseenBrushProperty = DependencyProperty.Register(
            nameof(UnseenBrush), typeof(Brush), typeof(StatusRing),
            new FrameworkPropertyMetadata(MakeBrush(0x1D, 0xAA, 0x61), FrameworkPropertyMetadataOptions.AffectsRender));

        public int Total { get => (int)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
        public int Seen { get => (int)GetValue(SeenProperty); set => SetValue(SeenProperty, value); }
        public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
        public Brush SeenBrush { get => (Brush)GetValue(SeenBrushProperty); set => SetValue(SeenBrushProperty, value); }
        public Brush UnseenBrush { get => (Brush)GetValue(UnseenBrushProperty); set => SetValue(UnseenBrushProperty, value); }

        private static Point PointAt(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            int n = Total;
            if (n <= 0 || w <= 0 || h <= 0) return;

            double t = RingThickness;
            double r = Math.Min(w, h) / 2 - t / 2;
            var center = new Point(w / 2, h / 2);

            if (n == 1)
            {
                dc.DrawEllipse(null, new Pen(Seen >= 1 ? SeenBrush : UnseenBrush, t), center, r, r);
                return;
            }

            double slice = 360.0 / n;
            double gap = Math.Min(12.0, slice / 3.0);

            for (int i = 0; i < n; i++)
            {
                double a0 = -90 + i * slice + gap / 2;
                double a1 = a0 + slice - gap;

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(PointAt(center, r, a0), false, false);
                    ctx.ArcTo(PointAt(center, r, a1), new Size(r, r), 0, (a1 - a0) > 180,
                              SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();

                dc.DrawGeometry(null, new Pen(i < Seen ? SeenBrush : UnseenBrush, t), geo);
            }
        }
    }
}
