using System;
using System.Collections.Generic;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tesela
{
    /// <summary>
    /// Ventana sin bordes que aloja un widget pegado al escritorio: no sale en la barra de tareas
    /// ni en Alt+Tab, se queda siempre por debajo del resto de ventanas y sobrevive a Win+D.
    /// </summary>
    public class WidgetHost : Window
    {
        // Medidas lógicas (a escala 1). La escala global de Ajustes se aplica con un LayoutTransform.
        public const double Unit = 160;    // lado del widget pequeño
        public const double Gap = 20;      // separación entre widgets
        public const double Pad = 16;      // margen transparente para la sombra
        public const double Margin0 = 28;  // margen respecto al borde de la pantalla
        public const double Radius = 24;   // radio por defecto (Ajustes lo cambia)

        public Theme CurrentTheme { get; private set; }

        public readonly Widget Widget;
        public readonly AppController App;
        readonly Border card, shadow;
        readonly Grid root, frame;
        bool resizing;
        readonly DispatcherTimer timer = new DispatcherTimer();
        IntPtr hwnd;
        BackdropLayer glassBase;

        public WidgetHost(Widget widget, AppController app)
        {
            Widget = widget;
            App = app;
            widget.Host = this;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Title = "Tesela";
            UseLayoutRounding = true;
            Ui.TextQuality(this);

            shadow = new Border
            {
                Background = Brushes.Black,
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.28 },
            };
            card = new Border();
            card.SizeChanged += (s, e) => UpdateClip();

            // frame = sombra + tarjeta; al cambiar de tamaño se anima su posición y su tamaño.
            frame = new Grid { Margin = new Thickness(Pad), Children = { shadow, card } };
            root = new Grid { Children = { frame } };
            Content = root;

            ApplyAppearance(false);

            timer.Tick += (s, e) => Refresh();
            MouseLeftButtonDown += OnDragStart;
            MouseRightButtonUp += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
                e.Handled = true;
                MenuWindow.ShowAtMouse(BuildMenu());
            };
            LocationChanged += (s, e) => UpdateGlass();
            Loaded += (s, e) => { Refresh(); widget.Start(); PopIn(); };
            Closed += (s, e) => { timer.Stop(); widget.Stop(); };
            // Si alguien cierra la ventana desde fuera (Finalizar tarea…), se guarda antes
            Closing += (s, e) => app.SaveNow();
        }

        public static Size Dimensions(WidgetSize s)
        {
            double big = Unit * 2 + Gap;
            return s == WidgetSize.Small ? new Size(Unit, Unit)
                : s == WidgetSize.Medium ? new Size(big, Unit)
                : new Size(big, big);
        }

        double S => App.State.Scale;

        public static Size ScaledDimensions(WidgetSize s, double scale)
        {
            var d = Dimensions(s);
            return new Size(d.Width * scale, d.Height * scale);
        }

        /// <summary>
        /// Posición de la tarjeta dentro de la ventana (DIPs sin escalar). Normalmente (Pad, Pad). Tras un
        /// cambio de tamaño la ventana se queda algo más grande que la tarjeta (la parte sobrante es
        /// transparente y deja pasar los clics): así el final de la animación no mueve ni redimensiona la
        /// ventana, que era lo que producía un salto.
        /// </summary>
        Point cardOffset = new Point(Pad, Pad);

        /// <summary>Rectángulo de la tarjeta (sin la sombra) en DIPs, ya escalado. Durante un cambio de tamaño, el de destino.</summary>
        public Rect CardRect => resizing ? animTo
            : new Rect(new Point(Left + cardOffset.X * S, Top + cardOffset.Y * S), ScaledDimensions(Widget.State.Size, S));

        const double ShadowInset = 2;

        void UpdateClip()
        {
            double r = App.State.Radius;
            Ui.RoundClip(card, card.ActualWidth, card.ActualHeight, r);
        }

        /// <summary>Aplica escala, esquinas, sombra y opacidad globales.</summary>
        public void ApplyAppearance(bool refresh = true)
        {
            var st = App.State;
            root.LayoutTransform = Math.Abs(st.Scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(st.Scale, st.Scale);
            card.CornerRadius = new CornerRadius(st.Radius);
            // La silueta que proyecta la sombra queda 2 px por dentro de la tarjeta: así no asoma en negro
            // por el borde suavizado de las esquinas (se notaba sobre todo con Liquid Glass).
            shadow.Margin = new Thickness(ShadowInset);
            shadow.CornerRadius = new CornerRadius(Math.Max(0, st.Radius - ShadowInset));
            shadow.Visibility = st.Shadow ? Visibility.Visible : Visibility.Hidden;
            root.Opacity = st.Opacity;
            UpdateClip();
            ApplySize();
            if (refresh) Refresh();
        }

        public void ApplySize()
        {
            FinishResize();
            // La ventana ya tiene sitio para el tamaño grande anclado arriba a la derecha (la parte que no
            // ocupa la tarjeta es transparente y deja pasar los clics). Así, cambiar de tamaño no tiene que
            // redimensionar la ventana a mitad de la animación.
            double sc = S;
            var dims = ScaledDimensions(Widget.State.Size, sc);
            var cardR = new Rect(Widget.State.X, Widget.State.Y, dims.Width, dims.Height);
            var big = ScaledDimensions(WidgetSize.Large, sc);
            var cover = Rect.Union(cardR, new Rect(cardR.Right - big.Width, cardR.Top, big.Width, big.Height));
            cardOffset = new Point(Pad + (cardR.X - cover.X) / sc, Pad + (cardR.Y - cover.Y) / sc);
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.VerticalAlignment = VerticalAlignment.Top;
            frame.Margin = new Thickness(cardOffset.X, cardOffset.Y, 0, 0);
            frame.Width = dims.Width / sc;
            frame.Height = dims.Height / sc;
            Left = cover.X - Pad * sc;
            Top = cover.Y - Pad * sc;
            Width = cover.Width + Pad * 2 * sc;
            Height = cover.Height + Pad * 2 * sc;
            StartTimer();
        }

        void StartTimer()
        {
            var iv = Widget.Interval;
            timer.Stop();
            if (iv.HasValue)
            {
                timer.Interval = iv.Value;
                timer.Start();
            }
        }

        /// <summary>Repinta el widget. <paramref name="fade"/>: el aspecto anterior se desvanece sobre el nuevo.</summary>
        // Foto del aspecto anterior mientras se desvanece (null si no hay fundido en marcha)
        Image fadeImg;

        public void Refresh(bool fade = false)
        {
            if (resizing) return;
            var size = Widget.State.Size;
            var t = ThemeFor(Widget, App.Theme, App.State);
            // Al pasar de Sólido a Liquid Glass (o al revés) el aspecto anterior se desvanece sobre el nuevo.
            // Si ya hay un fundido en marcha (p. ej. arrastrando un slider de color, que repinta muchas veces
            // seguidas) no se hace otra foto: la que se está desvaneciendo sigue encima del contenido nuevo.
            // Fotografiar el widget a mitad de fundido metería capas dentro de capas.
            bool wantFade = IsLoaded && CurrentTheme != null && (fade || CurrentTheme.Glass != t.Glass);
            bool fading = fadeImg != null && fadeImg.Opacity > 0.02;
            var before = wantFade && !fading ? Snapshot() : null;
            CurrentTheme = t;
            card.Background = CardBackground(Widget, t);
            var content = RenderContent(Widget, t, size);
            UIElement child;
            if (t.Glass)
            {
                child = Glassify(t, App.State.Radius, content, out glassBase);
            }
            else
            {
                glassBase = null;
                child = content;
            }
            if (before != null)
            {
                var img = new Image
                {
                    Source = before, Stretch = Stretch.None, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                fadeImg = img;
                card.Child = new Grid { Children = { child, img } };
                Motion.To(img, OpacityProperty, 0, 360, Motion.InOut, done: () =>
                {
                    (VisualTreeHelper.GetParent(img) as Panel)?.Children.Remove(img);
                    if (fadeImg == img) fadeImg = null;
                });
            }
            else if (fading)
            {
                // La foto que se desvanece pasa encima del contenido nuevo y sigue donde iba
                var img = fadeImg;
                (VisualTreeHelper.GetParent(img) as Panel)?.Children.Remove(img);
                card.Child = new Grid { Children = { child, img } };
            }
            else card.Child = child;
            UpdateGlass();
            ((DropShadowEffect)shadow.Effect).Opacity = ShadowOpacity(t);
        }

        /// <summary>
        /// Recoloca el fondo de pantalla bajo el widget translúcido. <paramref name="fade"/>: el fondo de
        /// pantalla ha cambiado y el material pasa al nuevo con un fundido.
        /// </summary>
        public void UpdateGlass(bool fade = false)
        {
            if (glassBase == null || !IsLoaded) return;
            double s = Ui.DpiScale;
            var r = CardRect;
            glassBase.Set(Backdrop.BrushFor(new Rect(r.X * s, r.Y * s, r.Width * s, r.Height * s), glassBase.Level), fade);
        }

        /// <summary>Apariencia del widget: "solid" o "glass" (la suya propia o, si no tiene, la general).</summary>
        public static string MaterialOf(Widget w, AppState st) => w.Get("material", st.Material == "glass" ? "glass" : "solid");

        /// <summary>Intensidad de Liquid Glass: es global, la misma para todos los widgets.</summary>
        public static double GlassIntensityOf(Widget w, AppState st) => Ui.Clamp(st.GlassIntensity, 0, 1);

        public static Theme ThemeFor(Widget w, Theme global, AppState st) =>
            global.ForWidget(w.Get("bg", "auto"), w.Get("bgStyle", "solid"), w.Get("bg2", "auto"), w.Get("bgDir", "vertical"),
                w.Get("accent", "auto"), w.DefaultAccentKey, w.AllowLightBackground,
                MaterialOf(w, st), st.AccentInWidgets ? Theme.SystemAccent() : (Color?)null,
                GlassIntensityOf(w, st));

        /// <summary>Con más transparencia, la sombra también se aligera: el cristal pesa menos.</summary>
        static double ShadowOpacity(Theme t) => t.Glass ? 0.32 - 0.1 * t.GlassLevel : 0.28;

        public static Brush CardBackground(Widget w, Theme t) =>
            t.Glass ? Brushes.Transparent : w.Get("bg", "auto") == "auto" ? w.Background(t) : t.Card;

        static byte A(double v) => (byte)Math.Round(Ui.Clamp(v, 0, 1) * 255);
        static double Lerp(double a, double b, double k) => a + (b - a) * k;

        /// <summary>
        /// Liquid Glass: parte del fondo de pantalla real que hay detrás (lo coloca <see cref="UpdateGlass"/>).
        /// La intensidad (<see cref="Theme.GlassLevel"/>) gradúa a la vez todo el material:
        /// <list type="bullet">
        /// <item>Transparencia: el velo de legibilidad (o el color que tiñe el cristal) se aclara.</item>
        /// <item>Desenfoque: el fondo pasa de esmerilado a casi nítido (lo calcula <see cref="Backdrop"/>).</item>
        /// <item>Acabado: el reflejo especular, el canto de luz y el filo se hacen más vivos.</item>
        /// </list>
        /// Al mínimo es un cristal esmerilado discreto y muy legible; al máximo, claramente transparente.
        /// El contenido va entre las capas del material y el filo.
        /// </summary>
        public static FrameworkElement Glassify(Theme t, double radius, UIElement content, out BackdropLayer baseLayer)
        {
            double k = t.GlassLevel;
            baseLayer = new BackdropLayer { Level = Backdrop.GlassLevel(k) };
            var g = new Grid { Children = { baseLayer } };

            // Velo: color del widget si lo tiene; si no, claro u oscuro según el tema. Nunca blanco lechoso.
            Color veil;
            if (t.GlassTint.HasValue)
            {
                veil = t.GlassTint.Value;
                veil.A = A(Lerp(0.62, 0.26, k));
            }
            else veil = t.Dark ? Color.FromArgb(A(Lerp(0.5, 0.1, k)), 0, 0, 0) : Color.FromArgb(A(Lerp(0.6, 0.14, k)), 255, 255, 255);
            g.Children.Add(new Rectangle { Fill = new SolidColorBrush(veil), IsHitTestVisible = false });

            // Reflejo especular: una luz suave que entra por arriba a la izquierda.
            g.Children.Add(new Rectangle
            {
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0.7, 0.9),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(A(Lerp(0.06, 0.2, k)), 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(A(Lerp(0.01, 0.04, k)), 255, 255, 255), 0.35),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                    },
                },
            });
            // Grosor: sombra interior muy suave abajo.
            g.Children.Add(new Rectangle
            {
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.6),
                        new GradientStop(Color.FromArgb(A(t.Dark ? Lerp(0.08, 0.16, k) : Lerp(0.03, 0.07, k)), 0, 0, 0), 1),
                    },
                },
            });
            g.Children.Add(content);
            // Canto de luz: la que recoge el borde del cristal, fina y difuminada hacia dentro.
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Math.Max(0, radius - 1)), BorderThickness = new Thickness(2.5), Margin = new Thickness(1),
                IsHitTestVisible = false, Opacity = Lerp(0.35, 1, k),
                BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.35),
                        new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.7),
                        new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 1),
                    },
                },
                Effect = new BlurEffect { Radius = 3 },
            });
            // Filo: una línea de luz de 1 px, más viva arriba a la izquierda y abajo a la derecha.
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), IsHitTestVisible = false,
                BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(A(Lerp(0.32, 0.62, k)), 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(A(Lerp(0.06, 0.11, k)), 255, 255, 255), 0.3),
                        new GradientStop(Color.FromArgb(A(Lerp(0.03, 0.06, k)), 255, 255, 255), 0.7),
                        new GradientStop(Color.FromArgb(A(Lerp(0.16, 0.34, k)), 255, 255, 255), 1),
                    },
                },
            });
            return g;
        }

        /// <summary>Tarjeta suelta (sin ventana) para vistas previas. Con cristal, la base queda en Tag.</summary>
        public static FrameworkElement BuildCard(Widget w, WidgetSize size, Theme t, double radius, bool withShadow)
        {
            var d = Dimensions(size);
            var grid = new Grid { Width = d.Width, Height = d.Height };
            if (withShadow)
                grid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(Math.Max(0, radius - ShadowInset)), Background = Brushes.Black, Margin = new Thickness(ShadowInset),
                    Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = ShadowOpacity(t) },
                });
            UIElement content = RenderContent(w, t, size);
            if (t.Glass)
            {
                content = Glassify(t, radius, content, out var baseLayer);
                grid.Tag = baseLayer;
            }
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = CardBackground(w, t),
                Child = content,
            };
            Ui.RoundClip(cardBorder, d.Width, d.Height, radius);
            grid.Children.Add(cardBorder);
            return grid;
        }

        public static UIElement RenderContent(Widget w, Theme t, WidgetSize size)
        {
            UIElement content;
            try { content = w.Render(t, size); }
            catch (Exception ex) { content = Ui.Text("Error: " + ex.Message, 11, t.Red); }
            return new Border { Padding = w.Padding(size), Child = content };
        }

        // ---- Cambio de tamaño animado -----------------------------------------------------------------
        //
        // Un solo reloj por fotograma mueve y redimensiona la tarjeta (posición y tamaño a la vez, con la
        // misma curva y sin rebote). El contenido nuevo se maqueta ya a su tamaño final y la tarjeta lo va
        // descubriendo, así nada se deforma; el aspecto anterior se desvanece encima. El fondo de Liquid
        // Glass se recoloca en cada fotograma para que siga a la tarjeta. Si se pide otro tamaño a mitad de
        // camino, la animación sale desde donde está (sin terminar la anterior).

        const double ResizeMs = 380;
        Rect animFrom, animTo, animNow;
        DateTime animStart;
        bool frameHooked;

        public void SetSize(WidgetSize size)
        {
            if (size == Widget.State.Size) return;
            double sc = S;
            var cur = resizing ? animNow : CardRect;
            var oldSnap = Snapshot();

            Widget.State.Size = size;
            // La cuadrícula está anclada a la derecha: se conserva el borde derecho y el superior, así
            // el widget crece hacia la izquierda y al encogerse vuelve exactamente a su sitio.
            var dims = ScaledDimensions(size, sc);
            var target = App.FindFreeSlot(size, this, new Point(cur.Right - dims.Width, cur.Top));
            var to = new Rect(target, dims);

            // La ventana debe contener la tarjeta actual y la de destino. Solo crece (nunca a mitad de una
            // animación se encoge) y, de paso, deja sitio al tamaño grande, para que los siguientes cambios
            // no tengan que tocarla.
            var win = new Rect(Left, Top, Width, Height);
            var need = Rect.Union(cur, to);
            var big = ScaledDimensions(WidgetSize.Large, sc);
            need.Union(new Rect(cur.Right - big.Width, cur.Top, big.Width, big.Height));
            need.Inflate(Pad * sc, Pad * sc);
            if (!win.Contains(need))
            {
                win.Union(need);
                Left = win.X;
                Top = win.Y;
                Width = win.Width;
                Height = win.Height;
            }

            animFrom = animNow = cur;
            animTo = to;
            animStart = DateTime.Now;
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.VerticalAlignment = VerticalAlignment.Top;
            PlaceFrame(cur);

            // Contenido nuevo a su tamaño final, anclado arriba a la izquierda, + instantánea del anterior encima.
            resizing = false;
            Refresh();
            resizing = true;
            timer.Stop();
            var newD = Dimensions(size);
            var fresh = (UIElement)card.Child;
            card.Child = null;
            var holder = new Grid
            {
                Width = newD.Width, Height = newD.Height, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Children = { fresh },
            };
            var layers = new Grid { Children = { holder } };
            if (oldSnap != null)
            {
                var img = new Image
                {
                    Source = oldSnap, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                };
                layers.Children.Add(img);
                Motion.To(img, OpacityProperty, 0, 260, Motion.InOut, done: () => layers.Children.Remove(img));
            }
            // El contenido nuevo está opaco debajo desde el principio y solo se desvanece el anterior encima:
            // la tarjeta nunca pasa por un momento semitransparente (también al cambiar de dirección).
            card.Child = layers;
            ResizeFrame();

            if (!frameHooked)
            {
                CompositionTarget.Rendering += OnResizeFrame;
                frameHooked = true;
            }
        }

        void PlaceFrame(Rect r)
        {
            double sc = S;
            frame.Margin = new Thickness((r.X - Left) / sc, (r.Y - Top) / sc, 0, 0);
            frame.Width = r.Width / sc;
            frame.Height = r.Height / sc;
        }

        void OnResizeFrame(object sender, EventArgs e)
        {
            if (!resizing) return;
            ResizeFrame();
            if ((DateTime.Now - animStart).TotalMilliseconds >= ResizeMs) FinishResize();
        }

        void ResizeFrame()
        {
            double k = Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / ResizeMs);
            double f = Motion.Out.Ease(k);
            var r = new Rect(
                animFrom.X + (animTo.X - animFrom.X) * f, animFrom.Y + (animTo.Y - animFrom.Y) * f,
                animFrom.Width + (animTo.Width - animFrom.Width) * f, animFrom.Height + (animTo.Height - animFrom.Height) * f);
            animNow = r;
            PlaceFrame(r);
            // El cristal del contenido nuevo (anclado arriba a la izquierda, a su tamaño final) sigue a la tarjeta
            if (glassBase != null)
            {
                double s = Ui.DpiScale;
                glassBase.Set(Backdrop.BrushFor(new Rect(r.X * s, r.Y * s, animTo.Width * s, animTo.Height * s), glassBase.Level));
            }
        }

        /// <summary>Termina el cambio de tamaño: la tarjeta queda en su sitio sin mover la ventana.</summary>
        void FinishResize()
        {
            if (frameHooked)
            {
                CompositionTarget.Rendering -= OnResizeFrame;
                frameHooked = false;
            }
            if (!resizing) return;
            resizing = false;
            double sc = S;
            PlaceFrame(animTo);
            cardOffset = new Point((animTo.X - Left) / sc, (animTo.Y - Top) / sc);
            Widget.State.X = animTo.X;
            Widget.State.Y = animTo.Y;
            StartTimer();
            Refresh();
            App.SaveSoon();
        }

        /// <summary>Imagen de la tarjeta tal y como se ve ahora (para el fundido al cambiar de tamaño).</summary>
        BitmapSource Snapshot()
        {
            try
            {
                double w = card.ActualWidth, h = card.ActualHeight;
                if (w <= 0 || h <= 0) return null;
                double k = S * Ui.DpiScale;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    // Viewbox fijo en los límites de la tarjeta: si algo del contenido sobresale (un brillo, un margen
                    // negativo), el pincel lo encuadraría por su contorno y la foto saldría desplazada unos píxeles.
                    dc.DrawRectangle(new VisualBrush(card)
                    {
                        Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h),
                    }, null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * k), (int)Math.Ceiling(h * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        void PopIn()
        {
            var st = new ScaleTransform(0.92, 0.92);
            ((FrameworkElement)Content).RenderTransform = st;
            ((FrameworkElement)Content).RenderTransformOrigin = new Point(0.5, 0.5);
            Motion.To(st, ScaleTransform.ScaleXProperty, 1, 340);
            Motion.To(st, ScaleTransform.ScaleYProperty, 1, 340, done: () =>
            {
                // Sin transformación animada viva: el texto vuelve a dibujarse con calidad completa.
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ((FrameworkElement)Content).RenderTransform = null;
            });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
        }

        // ---- Pegado al escritorio -------------------------------------------------------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;

            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            // Tener como propietario a la ventana del escritorio (Progman) hace que Win+D no la oculte.
            var progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);

            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            SendToBottom();
        }

        void SendToBottom() => Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if ((wp.flags & Native.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        // ---- Arrastrar y encajar en la cuadrícula ----------------------------------------------

        void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (App.State.Locked || e.ButtonState != MouseButtonState.Pressed) return;
            if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
            Opacity = 0.85;
            try { DragMove(); } catch (InvalidOperationException) { }
            Opacity = 1;
            var target = App.FindFreeSlot(Widget.State.Size, this, CardRect.TopLeft);
            AnimateCardTo(target);
            App.SaveNow();
        }

        static bool IsInteractive(DependencyObject d)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is TextBoxBase || d is ButtonBase) return true;
            return false;
        }

        public void MoveCardTo(Point p)
        {
            Left = p.X - cardOffset.X * S;
            Top = p.Y - cardOffset.Y * S;
            Widget.State.X = p.X;
            Widget.State.Y = p.Y;
        }

        void AnimateCardTo(Point p)
        {
            double fromX = Left, fromY = Top, toX = p.X - cardOffset.X * S, toY = p.Y - cardOffset.Y * S;
            Widget.State.X = p.X;
            Widget.State.Y = p.Y;
            var start = DateTime.Now;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            t.Tick += (s, e) =>
            {
                double k = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 200);
                double ease = 1 - Math.Pow(1 - k, 3);
                Left = fromX + (toX - fromX) * ease;
                Top = fromY + (toY - fromY) * ease;
                if (k >= 1) t.Stop();
            };
            t.Start();
        }

        // ---- Menú contextual ---------------------------------------------------------------------

        List<MenuItemSpec> BuildMenu()
        {
            var m = new List<MenuItemSpec>
            {
                new MenuItemSpec { Text = "Personalizar…", Bold = true, Click = () => App.OpenSettings(this) },
                MenuItemSpec.Separator,
            };
            foreach (WidgetSize s in Enum.GetValues(typeof(WidgetSize)))
            {
                var size = s;
                m.Add(new MenuItemSpec { Text = WidgetCatalog.SizeName(s), Checked = Widget.State.Size == s, Click = () => SetSize(size) });
            }
            var extra = new List<MenuItemSpec>();
            Widget.AddMenuItems(extra);
            if (extra.Count > 0)
            {
                m.Add(MenuItemSpec.Separator);
                m.AddRange(extra);
            }
            m.Add(MenuItemSpec.Separator);
            m.Add(new MenuItemSpec { Text = "Añadir widget", Items = App.AddMenu() });
            m.Add(new MenuItemSpec { Text = "Bloquear posiciones", Checked = App.State.Locked, Click = () => App.SetLocked(!App.State.Locked) });
            m.Add(MenuItemSpec.Separator);
            m.Add(new MenuItemSpec { Text = "Quitar widget", Danger = true, Click = () => App.RemoveWidget(this) });
            return m;
        }
    }
}
