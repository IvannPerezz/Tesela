using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tesela
{
    /// <summary>Curva cubic-bezier(x1, y1, x2, y2), igual que en CSS.</summary>
    public class CubicBezierEase : EasingFunctionBase
    {
        public double X1, Y1, X2, Y2;

        public CubicBezierEase() { EasingMode = EasingMode.EaseIn; }

        public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        protected override double EaseInCore(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            // x(s) es monótona: bisección para encontrar s con x(s) = t
            double lo = 0, hi = 1, s = t;
            for (int i = 0; i < 22; i++)
            {
                double x = Bez(s, X1, X2);
                if (Math.Abs(x - t) < 1e-6) break;
                if (x < t) lo = s; else hi = s;
                s = (lo + hi) / 2;
            }
            return Bez(s, Y1, Y2);
        }

        static double Bez(double s, double a, double b) =>
            3 * a * s * (1 - s) * (1 - s) + 3 * b * s * s * (1 - s) + s * s * s;

        protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);
    }

    /// <summary>Animaciones de la interfaz: fluidas, cortas y sin rebote.</summary>
    static class Motion
    {
        /// <summary>Salida suave (la de BitChord): arranca rápido y frena sin pasarse.</summary>
        public static readonly IEasingFunction Out = Make(.2, .8, .2, 1);
        /// <summary>Para lo que se va: acelera hacia el final.</summary>
        public static readonly IEasingFunction In = Make(.4, 0, 1, 1);
        public static readonly IEasingFunction InOut = Make(.45, 0, .55, 1);

        static IEasingFunction Make(double a, double b, double c, double d)
        {
            var e = new CubicBezierEase(a, b, c, d);
            e.Freeze();
            return e;
        }

        public static DoubleAnimation D(double to, double ms, IEasingFunction ease = null, double? from = null, double delay = 0) =>
            new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = ease ?? Out,
            };

        public static void To(IAnimatable target, DependencyProperty dp, double to, double ms,
            IEasingFunction ease = null, double? from = null, double delay = 0, Action done = null)
        {
            var a = D(to, ms, ease, from, delay);
            if (done != null) a.Completed += (s, e) => done();
            target.BeginAnimation(dp, a);
        }

        public static void Color(SolidColorBrush b, Color to, double ms = 220)
        {
            if (b.IsFrozen) return;
            b.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
        }

        /// <summary>Pone un color al instante (cancela cualquier animación previa).</summary>
        public static void SetColor(SolidColorBrush b, Color c)
        {
            if (b.IsFrozen) return;
            b.BeginAnimation(SolidColorBrush.ColorProperty, null);
            b.Color = c;
        }
    }
}
