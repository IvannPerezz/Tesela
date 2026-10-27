using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Tesela
{
    /// <summary>
    /// "Reproduciendo": usa los controles multimedia de Windows (GlobalSystemMediaTransportControls),
    /// así funciona con Spotify, el navegador, Reproductor multimedia, etc.
    /// </summary>
    public class MediaWidget : Widget
    {
        static GlobalSystemMediaTransportControlsSessionManager manager;
        static readonly Brush White = Brushes.White;
        static readonly Brush Soft = Ui.Hex("#B3FFFFFF");

        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        TimeSpan position, duration;
        DateTime positionAt;
        GlobalSystemMediaTransportControlsSession session;
        string title, artist, source, lastKey;
        bool playing, busy;
        BitmapSource art;
        Brush artBackground;

        // Animación de baraja al cambiar de canción (como en BitChord)
        BitmapSource deckOld;
        DateTime deckStart = DateTime.MinValue;
        int deckDir = 1, pendingDir = 1;
        const double DeckMs = 880;
        // Fundido del fondo (los colores de la carátula) al cambiar de canción: solo el fondo, por detrás
        Brush bgOld;
        DateTime bgStart = DateTime.MinValue;
        const double BgMs = 700;
        // Fundido de la carátula cuando llega una mejor de internet
        BitmapSource artFadeOld;
        DateTime artFadeStart = DateTime.MinValue;
        const double ArtFadeMs = 420;
        // Momento del último reproducir/pausa (el icono nuevo entra con una animación)
        DateTime toggledAt = DateTime.MinValue;
        // Momento en que se reanudó la reproducción (la barra cuenta desde aquí si la app no avisa)
        DateTime resumedAt = DateTime.MinValue;
        const double ToggleGraceMs = 1500;
        // El ratón está sobre el botón de reproducir (para que el botón repintado salga igual)
        bool playHover;

        public override string Kind => "media";
        public override TimeSpan? Interval => null;
        public override Thickness Padding(WidgetSize size) => new Thickness(Inset);
        public override bool UsesAccent => false;
        public override bool AllowLightBackground => false;

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Reproducción");
            c.Toggle("Barra de progreso", "progress", true);
            c.Section("Carátulas");
            c.Toggle("Buscar la carátula en internet si falta o es de baja calidad", "onlineArt", true);
            c.Hint("Se busca en el catálogo de iTunes con el título y el artista (solo cuando la app no da una buena portada).");
        }

        public override void BuildColors(SettingsContext c)
        {
            c.Section("Carátula");
            c.Toggle("Fondo con los colores de la carátula", "artColor", true);
            c.Hint("Con el fondo automático, el widget toma los colores de la canción (difuminados).");
        }

        public override void Start()
        {
            poll.Tick += async (s, e) => await Poll();
            poll.Start();
            _ = Poll();
        }

        public override void Stop() => poll.Stop();

        async Task Poll()
        {
            if (busy) return;
            busy = true;
            try
            {
                if (manager == null)
                    manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var s = manager.GetCurrentSession();
                if (s == null)
                {
                    if (session != null || lastKey != null)
                    {
                        session = null;
                        lastKey = null;
                        Invalidate();
                    }
                    return;
                }

                var props = await s.TryGetMediaPropertiesAsync();
                bool isPlaying = s.GetPlaybackInfo().PlaybackStatus ==
                                 GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string key = props.Title + "|" + props.Artist + "|" + s.SourceAppUserModelId;
                // Justo después de pulsar reproducir/pausa la app aún puede dar el estado anterior: durante
                // un momento manda lo pulsado, para que el icono no vuelva atrás y luego otra vez adelante.
                bool settling = (DateTime.Now - toggledAt).TotalMilliseconds < ToggleGraceMs && key == lastKey;
                if (settling) isPlaying = playing;
                bool changed = key != lastKey || isPlaying != playing || session == null;
                if (isPlaying && !playing) resumedAt = DateTime.Now;
                session = s;
                playing = isPlaying;
                try
                {
                    var tl = s.GetTimelineProperties();
                    duration = tl.EndTime - tl.StartTime;
                    // Mientras se asienta lo pulsado, la barra sigue con la posición propia (la de la app aún
                    // puede ser la de antes de pulsar)
                    if (!settling)
                    {
                        var at = tl.LastUpdatedTime.LocalDateTime;
                        if (at > DateTime.Now || at.Year < 2000) at = DateTime.Now;
                        // Muchas apps no actualizan la línea de tiempo al reanudar: su última actualización es
                        // de cuando se pausó y contar desde ahí haría saltar la barra hacia delante. Se cuenta
                        // desde que se reanudó.
                        if (playing && at < resumedAt) at = resumedAt;
                        position = tl.Position - tl.StartTime;
                        positionAt = at;
                    }
                }
                catch { duration = TimeSpan.Zero; }
                // Mientras suena, se repinta cada segundo para que avance la barra de progreso.
                if (playing && duration > TimeSpan.Zero && GetBool("progress", true)) changed = true;

                bool newTrack = false;
                if (key != lastKey)
                {
                    bool hadTrack = lastKey != null;
                    lastKey = key;
                    title = string.IsNullOrEmpty(props.Title) ? "Sin título" : props.Title;
                    artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumTitle : props.Artist;
                    source = AppName(s.SourceAppUserModelId);
                    var oldArt = art;
                    var oldBg = artBackground;
                    var (img, w, h) = await LoadArt(props.Thumbnail);
                    // Carátula pobre o ausente: se busca una mejor en internet. Se espera un momento (si
                    // llega a tiempo, la baraja ya sale con la buena); si tarda más, se cambia luego con un fundido.
                    if (GetBool("onlineArt", true) && NeedsBetterArt(img, w, h))
                    {
                        var search = FindOnlineArt(key, title, artist);
                        if (await Task.WhenAny(search, Task.Delay(1500)) == search && search.Result != null) img = search.Result;
                        else _ = LateOnlineArt(search, key);
                    }
                    art = img;
                    artBackground = art != null ? BackgroundFrom(art) : null;
                    if (hadTrack && oldBg != null)
                    {
                        bgOld = oldBg;
                        bgStart = DateTime.Now;
                    }
                    // Canción nueva: la carátula se baraja (hacia delante, o hacia atrás si se pulsó "anterior")
                    if (hadTrack && oldArt != null && art != null)
                    {
                        deckOld = oldArt;
                        deckStart = DateTime.Now;
                        deckDir = pendingDir;
                        newTrack = true;
                    }
                    pendingDir = 1;
                }
                if (newTrack || changed) Invalidate();
            }
            catch (Exception ex) { Program.Log("media: " + ex.Message); }
            finally { busy = false; }
        }

        /// <summary>Carátula que da la app que suena, con su tamaño original (para saber si es buena).</summary>
        static async Task<(BitmapSource Image, int Width, int Height)> LoadArt(IRandomAccessStreamReference thumb)
        {
            if (thumb == null) return (null, 0, 0);
            try
            {
                using (var ras = await thumb.OpenReadAsync())
                using (var stream = ras.AsStreamForRead())
                {
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    return Decode(ms.ToArray());
                }
            }
            catch { return (null, 0, 0); }
        }

        static (BitmapSource Image, int Width, int Height) Decode(byte[] data)
        {
            var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (w > 640) bmp.DecodePixelWidth = 640;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, w, h);
        }

        // ---- Carátulas de internet (iTunes) --------------------------------------------------------------
        //
        // Si la app que suena no da carátula, la da muy pequeña o no es cuadrada (la miniatura de un vídeo,
        // por ejemplo), se busca la portada en el catálogo de iTunes por título y artista. Solo se acepta
        // si el resultado coincide de verdad con la canción; si no, se queda la de la app.

        static readonly System.Net.Http.HttpClient http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        static readonly System.Collections.Generic.Dictionary<string, byte[]> onlineCache =
            new System.Collections.Generic.Dictionary<string, byte[]>();

        static bool NeedsBetterArt(BitmapSource img, int w, int h) =>
            img == null || Math.Min(w, h) < 300 || Math.Abs((double)w / Math.Max(1, h) - 1) > 0.12;

        /// <summary>Busca la carátula en internet (con caché, también de los fallos) y la decodifica.</summary>
        async Task<BitmapSource> FindOnlineArt(string key, string songTitle, string songArtist)
        {
            try
            {
                byte[] data;
                if (!onlineCache.TryGetValue(key, out data))
                {
                    data = await SearchItunes(songTitle, songArtist);
                    onlineCache[key] = data;
                }
                return data == null ? null : Decode(data).Image;
            }
            catch (Exception ex) { Program.Log("carátula online: " + ex.Message); return null; }
        }

        /// <summary>La carátula de internet ha llegado tarde: se cambia con un fundido (carátula y fondo).</summary>
        async Task LateOnlineArt(Task<BitmapSource> search, string key)
        {
            var img = await search;
            if (img == null || key != lastKey) return;
            artFadeOld = art;
            artFadeStart = DateTime.Now;
            bgOld = artBackground;
            bgStart = DateTime.Now;
            art = img;
            artBackground = BackgroundFrom(img);
            Invalidate();
        }

        /// <summary>
        /// Capa que se desvanece por tiempo (sigue donde iba aunque el widget se repinte cada segundo).
        /// </summary>
        static void FadeOut(UIElement layer, DateTime start, double ms)
        {
            double Op() => Math.Max(0, 1 - Motion.InOut.Ease(Math.Min(1, (DateTime.Now - start).TotalMilliseconds / ms)));
            layer.Opacity = Op();
            EventHandler tick = null;
            tick = (o, e) =>
            {
                layer.Opacity = Op();
                if (layer.Opacity <= 0) CompositionTarget.Rendering -= tick;
            };
            if (layer is FrameworkElement fe)
            {
                fe.Loaded += (o, e) => CompositionTarget.Rendering += tick;
                fe.Unloaded += (o, e) => CompositionTarget.Rendering -= tick;
            }
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Fuera lo que añaden los vídeos: (Official Video), [Lyrics], ft. …, "- Topic"…
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\(\[][^\)\]]*[\)\]]", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(official|video|audio|lyrics?|visualizer|remaster(ed)?|hd|4k|mv)\b", " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s(ft\.?|feat\.?|featuring)\s.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = s.Replace(" - Topic", "").Replace("VEVO", "");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '|', '·');
        }

        static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(char.IsLetterOrDigit).ToArray());

        static bool Similar(string a, string b)
        {
            a = Norm(a);
            b = Norm(b);
            return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
        }

        internal static async Task<byte[]> SearchItunes(string songTitle, string songArtist)
        {
            string t = Clean(songTitle), a = Clean(songArtist);
            // Vídeos con el título "Artista - Canción" (el "artista" suele ser el canal)
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            var tries = new System.Collections.Generic.List<(string Title, string Artist)> { (t, a) };
            if (dash > 0) tries.Insert(0, (t.Substring(dash + 3).Trim(), t.Substring(0, dash).Trim()));
            foreach (var (title, artist) in tries)
            {
                if (title.Length == 0) continue;
                var url = "https://itunes.apple.com/search?media=music&entity=song&limit=10&term=" + Uri.EscapeDataString((artist + " " + title).Trim());
                var json = await http.GetStringAsync(url);
                var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                if (!(root?["results"] is object[] results)) continue;
                foreach (System.Collections.Generic.Dictionary<string, object> r in results.OfType<System.Collections.Generic.Dictionary<string, object>>())
                {
                    var track = r.TryGetValue("trackName", out var tn) ? tn as string : null;
                    var who = r.TryGetValue("artistName", out var an) ? an as string : null;
                    var artUrl = r.TryGetValue("artworkUrl100", out var au) ? au as string : null;
                    if (artUrl == null || !Similar(track, title) || (artist.Length > 0 && !Similar(who, artist))) continue;
                    // La misma imagen a 600 × 600
                    return await http.GetByteArrayAsync(artUrl.Replace("100x100bb", "600x600bb"));
                }
            }
            return null;
        }

        /// <summary>
        /// Fondo como el de Música en iOS: la carátula muy desenfocada, algo más saturada por el
        /// desenfoque, y un velo oscuro que se intensifica hacia abajo para que el texto se lea bien.
        /// </summary>
        static Brush BackgroundFrom(BitmapSource bmp)
        {
            try
            {
                const int W = 160;
                var img = new Image
                {
                    Source = bmp, Stretch = Stretch.UniformToFill, Width = W + 80, Height = W + 80,
                    Margin = new Thickness(-40), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 26, KernelType = System.Windows.Media.Effects.KernelType.Gaussian },
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var host = new Grid { Width = W, Height = W, ClipToBounds = true, Children = { img } };
                host.Measure(new Size(W, W));
                host.Arrange(new Rect(0, 0, W, W));
                var rtb = new RenderTargetBitmap(W, W, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                rtb.Freeze();

                var veil = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x52, 0, 0, 0), 0),
                        new GradientStop(Color.FromArgb(0x8C, 0, 0, 0), 1),
                    },
                };
                var dg = new DrawingGroup();
                dg.Children.Add(new ImageDrawing(rtb, new Rect(0, 0, 1, 1)));
                dg.Children.Add(new GeometryDrawing(veil, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
                var brush = new DrawingBrush(dg) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                brush.Freeze();
                return brush;
            }
            catch { return null; }
        }

        static string AppName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var s = id.Split('!')[0];
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            s = s.Split('_')[0];
            var parts = s.Split('.');
            s = parts[parts.Length - 1];
            if (s.Equals("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Reproductor multimedia";
            if (s.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
            return Ui.Capitalize(s);
        }

        async void Control(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
        {
            if (session == null) return;
            try { await action(session); } catch { }
            await Task.Delay(250);
            await Poll();
        }

        // ---- Presentación ----------------------------------------------------------------------

        bool demo;

        /// <summary>Solo para --preview: congela la animación de baraja en un instante (ms desde el inicio).</summary>
        public void PreviewDeck(BitmapSource old, double ms, int dir)
        {
            deckOld = old;
            deckDir = dir;
            deckStart = DateTime.Now.AddMilliseconds(-ms);
        }

        /// <summary>Solo para --test-deck: simula un cambio de canción (baraja y fondo) sin tocar la música.</summary>
        public void TestTrackChange(BitmapSource other)
        {
            deckOld = other;
            deckDir = 1;
            deckStart = DateTime.Now;
            bgOld = BackgroundFrom(other);
            bgStart = DateTime.Now;
            Invalidate();
        }

        /// <summary>Solo para --preview: una canción de ejemplo cuando no suena nada.</summary>
        public void UseDemo(BitmapSource cover)
        {
            if (session != null) return;
            demo = true;
            title = "Canción de ejemplo con un título largo";
            artist = "Artista";
            art = cover;
            artBackground = BackgroundFrom(cover);
            duration = TimeSpan.FromSeconds(214);
            position = TimeSpan.FromSeconds(83);
            positionAt = DateTime.Now;
        }

        public override Brush Background(Theme t) =>
            (GetBool("artColor", true) ? artBackground : null) ?? Ui.Gradient("#3A3A3C", "#1C1C1E");

        static readonly Brush Muted = Ui.Hex("#99FFFFFF");

        double CardRadius => Host?.App.State.Radius ?? WidgetHost.Radius;

        /// <summary>
        /// Margen interior, igual en los cuatro lados. Crece con el redondeo de las esquinas para que nada
        /// quede pegado al contorno (de 16 a 22 px).
        /// </summary>
        double Inset => Math.Round(Ui.Clamp(CardRadius * 0.6, 16, 22));

        /// <summary>Radio concéntrico: el de la tarjeta menos el margen interior (como en iOS).</summary>
        double InnerRadius => Math.Max(4, CardRadius - Inset);

        static TextBlock Line(string text, double size, Brush brush, FontWeight weight, int lines = 1)
        {
            var tb = new TextBlock
            {
                Text = L.T(text ?? ""), FontSize = size, Foreground = brush, FontWeight = weight, FontFamily = Ui.For(size),
                TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = Math.Round(size * 1.2),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            if (lines > 1)
            {
                tb.TextWrapping = TextWrapping.Wrap;
                tb.MaxHeight = tb.LineHeight * lines;
            }
            return tb;
        }

        public override UIElement Render(Theme t, WidgetSize size)
        {
            var content = RenderContent(t, size);
            if (bgOld == null || (DateTime.Now - bgStart).TotalMilliseconds >= BgMs || t.Glass || !GetBool("artColor", true))
                return content;
            // El fondo de la canción anterior, por detrás de todo y cubriendo también el margen, se desvanece
            var old = new Border { Background = bgOld, Margin = new Thickness(-Inset), IsHitTestVisible = false };
            FadeOut(old, bgStart, BgMs);
            return new Grid { Children = { old, content } };
        }

        UIElement RenderContent(Theme t, WidgetSize size)
        {
            if (session == null && !demo) return Empty(size);
            switch (size)
            {
                case WidgetSize.Small:
                {
                    // Como el widget pequeño de Música: carátula arriba a la izquierda, botón arriba
                    // a la derecha y título/artista abajo, alineados a la izquierda.
                    var g = Ui.Rows(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
                    var top = Ui.Cols(Ui.Auto, Ui.Star(), Ui.Auto);
                    top.Children.Add(Ui.At(Art(64, InnerRadius), 0, 0));
                    var pp = PlayCircle(15);
                    pp.VerticalAlignment = VerticalAlignment.Top;
                    top.Children.Add(Ui.At(pp, 0, 2));
                    g.Children.Add(Ui.At(top, 0));
                    g.Children.Add(Ui.At(Line(title, 14, White, FontWeights.SemiBold), 2));
                    g.Children.Add(Ui.At(Line(artist, 13, Muted, FontWeights.Normal), 3));
                    return g;
                }

                case WidgetSize.Medium:
                {
                    // Carátula a toda altura, con esquinas concéntricas. A su lado, un único bloque centrado
                    // en vertical: título y artista, y justo debajo la barra de progreso y los controles.
                    double artSize = WidgetHost.Unit - Inset * 2;
                    var g = Ui.Cols(Ui.Px(artSize), Ui.Star());
                    g.Children.Add(Ui.At(Art(artSize, InnerRadius), 0, 0));
                    var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 2, 0) };
                    right.Children.Add(Line(title, 15, White, FontWeights.SemiBold, 2));
                    var ar = Line(artist, 13, Muted, FontWeights.Normal);
                    ar.Margin = new Thickness(0, 1, 0, 0);
                    right.Children.Add(ar);
                    var prog = Progress(false);
                    if (prog != null)
                    {
                        prog.Margin = new Thickness(0, 10, 0, 0);
                        right.Children.Add(prog);
                    }
                    var c = Controls(18);
                    c.Margin = new Thickness(0, prog != null ? 2 : 10, 0, 0);
                    right.Children.Add(c);
                    g.Children.Add(Ui.At(right, 0, 1));
                    return g;
                }

                default:
                {
                    // Como en el boceto: todo en una columna centrada y en armonía. La carátula grande arriba,
                    // el título y el artista debajo y, con el mismo ancho que la carátula (algo más), la barra de
                    // progreso y los controles. El conjunto se centra en vertical, con el mismo aire arriba y abajo.
                    double inner = WidgetHost.Unit * 2 + WidgetHost.Gap - Inset * 2;
                    double artSize = Math.Round(inner * 0.54);
                    double column = Math.Min(inner - 12, Math.Round(artSize * 1.45));
                    var g = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = column };
                    var cover = Art(artSize, InnerRadius);
                    cover.HorizontalAlignment = HorizontalAlignment.Center;
                    g.Children.Add(cover);
                    var ti = Line(title, 16, White, FontWeights.SemiBold);
                    ti.TextAlignment = TextAlignment.Center;
                    ti.Margin = new Thickness(0, 12, 0, 0);
                    var ar = Line(artist, 13.5, Muted, FontWeights.Normal);
                    ar.TextAlignment = TextAlignment.Center;
                    ar.Margin = new Thickness(0, 1, 0, 0);
                    g.Children.Add(ti);
                    g.Children.Add(ar);
                    var prog = Progress(true);
                    if (prog != null)
                    {
                        prog.Margin = new Thickness(0, 12, 0, 0);
                        g.Children.Add(prog);
                    }
                    var c = Controls(21);
                    c.Margin = new Thickness(0, prog != null ? 2 : 12, 0, 0);
                    g.Children.Add(c);
                    return g;
                }
            }
        }

        void TogglePlay()
        {
            // Respuesta inmediata: el icono cambia ya (con su animación) y el estado real llega después
            // La barra se queda exactamente donde está: al pausar se congela la posición actual y al
            // reanudar se cuenta desde ahora
            var now = DateTime.Now;
            if (playing) position += now - positionAt;
            positionAt = now;
            playing = !playing;
            if (playing) resumedAt = now;
            toggledAt = now;
            Invalidate();
            Control(s => s.TryTogglePlayPauseAsync());
        }

        static string Clock(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        /// <summary>Barra de progreso fina con el tiempo transcurrido y el restante, como en iOS.</summary>
        FrameworkElement Progress(bool large)
        {
            if (duration <= TimeSpan.Zero || !GetBool("progress", true)) return null;
            var pos = playing ? position + (DateTime.Now - positionAt) : position;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (pos > duration) pos = duration;
            double f = pos.TotalSeconds / duration.TotalSeconds;

            var bar = new Grid { Height = large ? 5 : 4 };
            bar.Children.Add(new Border { Background = Ui.Hex("#40FFFFFF"), CornerRadius = new CornerRadius(3) });
            var fill = Ui.Cols(Ui.Star(Math.Max(0.0001, f)), Ui.Star(Math.Max(0.0001, 1 - f)));
            fill.Children.Add(Ui.At(new Border { Background = Ui.Hex("#E6FFFFFF"), CornerRadius = new CornerRadius(3) }, 0, 0));
            bar.Children.Add(fill);

            var times = Ui.Cols(Ui.Star(), Ui.Star());
            times.Margin = new Thickness(0, 3, 0, 0);
            double fs = large ? 11.5 : 10.5;
            times.Children.Add(Ui.At(Line(Clock(pos), fs, Muted, FontWeights.Medium), 0, 0));
            var rem = Line("-" + Clock(duration - pos), fs, Muted, FontWeights.Medium);
            rem.HorizontalAlignment = HorizontalAlignment.Right;
            times.Children.Add(Ui.At(rem, 0, 1));
            return new StackPanel { Children = { bar, times } };
        }

        /// <summary>
        /// Carátula. Al cambiar de canción se baraja como en BitChord: la nueva sale de detrás de la anterior
        /// hacia un lado, inclinada, pasa por encima y se posa en su sitio, mientras la anterior se aparta,
        /// se hunde y se oscurece. Va por tiempo, así que sigue donde iba aunque el widget se repinte.
        /// </summary>
        FrameworkElement Art(double size, double radius)
        {
            double elapsed = (DateTime.Now - deckStart).TotalMilliseconds;
            if (deckOld == null || elapsed >= DeckMs)
            {
                var card = ArtCard(art, size, radius, out _);
                if (artFadeOld == null || (DateTime.Now - artFadeStart).TotalMilliseconds >= ArtFadeMs) return card;
                var prev = ArtCard(artFadeOld, size, radius, out _);
                FadeOut(prev, artFadeStart, ArtFadeMs);
                return new Grid
                {
                    Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Children = { card, prev },
                };
            }

            var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var under = ArtCard(deckOld, size, radius, out var underImg);
            var dim = new Border { CornerRadius = new CornerRadius(radius), Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            ((Grid)under).Children.Add(dim);
            var top = ArtCard(art, size, radius, out _);
            var tu = Xf(under);
            var tt = Xf(top);
            host.Children.Add(under);
            host.Children.Add(top);
            int dir = deckDir;
            var newEase = new CubicBezierEase(.45, .05, .35, 1);
            var oldEase = new CubicBezierEase(.42, 0, .58, 1);

            void Frame()
            {
                double t = Math.Min(1, (DateTime.Now - deckStart).TotalMilliseconds / DeckMs);
                double p = newEase.Ease(t), q = oldEase.Ease(t);
                // La nueva: 0 → .45 sale hacia el lado por detrás, .5 pasa por encima, .86 se posa
                double[] k = { 0, .45, .5, .86, 1 };
                Set(tt, size,
                    Key(p, k, 0, .68, .66, -.02, 0) * dir, Key(p, k, 0, -.04, -.05, -.01, 0),
                    Key(p, k, 0, 10, 9, -1.5, 0) * dir, Key(p, k, .94, .96, 1.02, 1.02, 1));
                Panel.SetZIndex(top, p < .5 ? 0 : 2);
                Panel.SetZIndex(under, 1);
                // La anterior: se aparta un poco y después se hunde y se oscurece
                double[] ko = { 0, .45, 1 };
                Set(tu, size, Key(q, ko, 0, -.06, 0) * dir, 0, Key(q, ko, 0, -2, 0) * dir, Key(q, ko, 1, 1, .95));
                dim.Opacity = Key(q, ko, 0, 0, .4);
                if (t >= 1) under.Visibility = Visibility.Collapsed;
            }
            EventHandler tick = null;
            tick = (o, e) =>
            {
                Frame();
                if ((DateTime.Now - deckStart).TotalMilliseconds >= DeckMs) CompositionTarget.Rendering -= tick;
            };
            Frame();
            host.Loaded += (o, e) => CompositionTarget.Rendering += tick;
            host.Unloaded += (o, e) => CompositionTarget.Rendering -= tick;
            return host;
        }

        static (ScaleTransform S, RotateTransform R, TranslateTransform T) Xf(FrameworkElement e)
        {
            var sc = new ScaleTransform();
            var rt = new RotateTransform();
            var tr = new TranslateTransform();
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            e.RenderTransform = new TransformGroup { Children = { sc, rt, tr } };
            return (sc, rt, tr);
        }

        static void Set((ScaleTransform S, RotateTransform R, TranslateTransform T) x, double size, double tx, double ty, double rot, double scale)
        {
            x.S.ScaleX = x.S.ScaleY = scale;
            x.R.Angle = rot;
            x.T.X = tx * size;
            x.T.Y = ty * size;
        }

        /// <summary>Interpolación lineal por tramos entre fotogramas clave (como los keyframes de la web).</summary>
        static double Key(double p, double[] at, params double[] v)
        {
            for (int i = 1; i < at.Length; i++)
                if (p <= at[i])
                {
                    double f = (p - at[i - 1]) / Math.Max(1e-6, at[i] - at[i - 1]);
                    return v[i - 1] + (v[i] - v[i - 1]) * f;
                }
            return v[v.Length - 1];
        }

        /// <summary>Carátula con esquinas concéntricas, sombra suave y un filo de luz de 0,5 px.</summary>
        FrameworkElement ArtCard(BitmapSource src, double size, double radius, out Border image)
        {
            var img = new Border { CornerRadius = new CornerRadius(radius) };
            if (src != null)
            {
                var brush = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                img.Background = brush;
            }
            else
            {
                img.Background = Ui.Hex("#48484A");
                img.Child = Ui.Icon(Ui.G(0xE8D6), size * 0.4, Soft);
            }
            image = img;
            var shadow = new Border
            {
                CornerRadius = new CornerRadius(radius), Background = Brushes.Black, Margin = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.35 },
            };
            var rim = new Border
            {
                CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(0.5),
                BorderBrush = Ui.Hex("#24FFFFFF"), IsHitTestVisible = false,
            };
            return new Grid
            {
                Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Children = { shadow, img, rim },
            };
        }

        FrameworkElement PlayPause(double size) => Ui.Button(Glyph(playing ? "pause" : "play", size), TogglePlay);

        /// <summary>
        /// Botón de reproducir/pausa dentro de un círculo translúcido. Al pasar el ratón el círculo crece un
        /// poco y se aclara; al pulsar se hunde; y el icono nuevo entra girando y creciendo.
        /// </summary>
        FrameworkElement PlayCircle(double size)
        {
            double d = Math.Round(size * 1.9);
            var glyph = Glyph(playing ? "pause" : "play", size * 1.4);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var fill = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255));
            var circle = new Border
            {
                Width = d, Height = d, CornerRadius = new CornerRadius(d / 2),
                Background = fill, BorderBrush = Ui.Hex("#26FFFFFF"), BorderThickness = new Thickness(0.75),
                Child = glyph, Cursor = Cursors.Hand,
            };
            var scale = new ScaleTransform(1, 1);
            circle.RenderTransformOrigin = new Point(0.5, 0.5);
            circle.RenderTransform = scale;
            void To(double v, double ms)
            {
                Motion.To(scale, ScaleTransform.ScaleXProperty, v, ms);
                Motion.To(scale, ScaleTransform.ScaleYProperty, v, ms);
            }
            // Al repintarse (justo al pulsar, y cada segundo mientras suena) el botón nuevo sale ya en el
            // estado del anterior: con el ratón encima sigue grande, y si se acaba de soltar sigue recuperándose.
            double since = (DateTime.Now - toggledAt).TotalMilliseconds;
            if (playHover)
            {
                fill.Color = Color.FromArgb(0x45, 255, 255, 255);
                scale.ScaleX = scale.ScaleY = 1.07;
            }
            if (since < 240)
            {
                double target = playHover ? 1.07 : 1;
                scale.ScaleX = scale.ScaleY = 0.9 + (target - 0.9) * Motion.Out.Ease(since / 240);
                To(target, 240 - since);
            }
            circle.MouseEnter += (o, e) => { playHover = true; To(1.07, 180); Motion.Color(fill, Color.FromArgb(0x45, 255, 255, 255), 180); };
            circle.MouseLeave += (o, e) =>
            {
                // Al repintar se quita el botón viejo (que también avisa de que el ratón "sale"): no cuenta
                if (PresentationSource.FromVisual(circle) == null) return;
                var m = Mouse.GetPosition(circle);
                if (m.X >= 0 && m.Y >= 0 && m.X <= d && m.Y <= d) return;
                playHover = false;
                To(1, 220); Motion.Color(fill, Color.FromArgb(0x2E, 255, 255, 255), 220); };
            circle.MouseLeftButtonDown += (o, e) => { e.Handled = true; To(0.9, 110); };
            circle.MouseLeftButtonUp += (o, e) =>
            {
                e.Handled = true;
                To(circle.IsMouseOver ? 1.07 : 1, 240);
                TogglePlay();
            };

            // El icono recién cambiado entra con un giro corto y creciendo (sin rebote). Va por tiempo: si el
            // widget se repinta a mitad, sigue donde iba en vez de empezar otra vez.
            if (since < 280)
            {
                double k = Motion.Out.Ease(since / 280);
                var gs = new ScaleTransform(0.4 + 0.6 * k, 0.4 + 0.6 * k);
                var gr = new RotateTransform(-70 * (1 - k));
                glyph.RenderTransformOrigin = new Point(0.5, 0.5);
                glyph.RenderTransform = new TransformGroup { Children = { gs, gr } };
                glyph.Opacity = Math.Min(1, since / 160);
                double rest = 280 - since;
                Motion.To(gs, ScaleTransform.ScaleXProperty, 1, rest);
                Motion.To(gs, ScaleTransform.ScaleYProperty, 1, rest);
                Motion.To(gr, RotateTransform.AngleProperty, 0, rest);
                if (since < 160) Motion.To(glyph, UIElement.OpacityProperty, 1, 160 - since);
            }
            return circle;
        }

        /// <summary>
        /// Anterior / siguiente: crecen un poco al pasar el ratón, se hunden al pulsar y el icono da un
        /// pequeño empujón hacia su lado, como si pasara la página.
        /// </summary>
        FrameworkElement SkipButton(string kind, double size, int dir, Action action)
        {
            var glyph = Glyph(kind, size);
            var push = new TranslateTransform();
            var holder = new Grid { Children = { glyph }, RenderTransform = push };
            var b = new Border { Background = Brushes.Transparent, Padding = new Thickness(6), Cursor = Cursors.Hand, Child = holder };
            var scale = new ScaleTransform(1, 1);
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = scale;
            void To(double v, double ms)
            {
                Motion.To(scale, ScaleTransform.ScaleXProperty, v, ms);
                Motion.To(scale, ScaleTransform.ScaleYProperty, v, ms);
            }
            b.MouseEnter += (o, e) => To(1.12, 180);
            b.MouseLeave += (o, e) => To(1, 220);
            b.MouseLeftButtonDown += (o, e) => { e.Handled = true; To(0.86, 110); };
            b.MouseLeftButtonUp += (o, e) =>
            {
                e.Handled = true;
                To(b.IsMouseOver ? 1.12 : 1, 240);
                Motion.To(push, TranslateTransform.XProperty, dir * size * 0.28, 110, Motion.Out, done: () =>
                    Motion.To(push, TranslateTransform.XProperty, 0, 220, Motion.Out));
                pendingDir = dir;
                action();
            };
            return b;
        }

        /// <summary>Anterior · reproducir/pausa · siguiente: centrados y en el mismo eje vertical.</summary>
        FrameworkElement Controls(double size)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var prev = SkipButton("prev", size * 1.05, -1, () => Control(s => s.TrySkipPreviousAsync()));
            var pp = PlayCircle(size);
            var next = SkipButton("next", size * 1.05, 1, () => Control(s => s.TrySkipNextAsync()));
            pp.Margin = new Thickness(size * 0.3, 0, size * 0.3, 0);
            foreach (var b in new[] { prev, pp, next })
            {
                b.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(b);
            }
            return sp;
        }

        // Los mismos iconos que el reproductor de BitChord (Material Symbols Rounded, caja de 24)
        static readonly System.Collections.Generic.Dictionary<string, Geometry> Glyphs = new System.Collections.Generic.Dictionary<string, Geometry>
        {
            ["play"] = Frozen("M8 17.18V6.83q0-.43.3-.71.3-.28.7-.28.13 0 .26.04.13.04.25.11l8.15 5.18q.23.15.34.38.11.23.11.45 0 .23-.11.45-.11.23-.34.38L9.5 18.02q-.12.08-.25.11-.13.04-.26.04-.4 0-.7-.28-.3-.28-.3-.71Z"),
            ["pause"] = Frozen("M15 19q-.82 0-1.41-.59Q13 17.83 13 17V7q0-.82.59-1.41Q14.18 5 15 5h2q.83 0 1.41.59Q19 6.18 19 7v10q0 .83-.59 1.41Q17.83 19 17 19Zm-8 0q-.82 0-1.41-.59Q5 17.83 5 17V7q0-.82.59-1.41Q6.18 5 7 5h2q.83 0 1.41.59Q11 6.18 11 7v10q0 .83-.59 1.41Q9.83 19 9 19Z"),
            ["next"] = Frozen("M17 17V7q0-.43.29-.71Q17.57 6 18 6t.71.29Q19 6.57 19 7v10q0 .43-.29.71-.28.29-.71.29t-.71-.29Q17 17.43 17 17ZM5 16.08V7.93q0-.45.3-.73.3-.28.7-.28.13 0 .28.03.15.03.27.13l6.13 4.1q.22.15.34.36.11.21.11.46t-.11.46q-.12.21-.34.36l-6.13 4.1q-.12.1-.27.13-.15.03-.28.03-.4 0-.7-.28-.3-.28-.3-.73Z"),
            ["prev"] = Frozen("M5 17V7q0-.43.29-.71Q5.58 6 6 6t.71.29Q7 6.57 7 7v10q0 .43-.29.71Q6.43 18 6 18t-.71-.29Q5 17.43 5 17Zm12.45-.4-6.13-4.1q-.22-.15-.34-.36-.11-.21-.11-.46t.11-.46q.12-.21.34-.36l6.13-4.1q.12-.1.27-.13.15-.03.28-.03.4 0 .7.28.3.28.3.73v8.15q0 .45-.3.73-.3.28-.7.28-.13 0-.28-.03-.15-.03-.27-.13Z"),
        };

        static Geometry Frozen(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        /// <summary>Icono de control relleno, escalado desde la caja de 24 de los iconos de BitChord.</summary>
        static FrameworkElement Glyph(string kind, double size)
        {
            var c = new Canvas { Width = size, Height = size };
            c.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Glyphs[kind], Fill = White,
                RenderTransform = new ScaleTransform(size / 24, size / 24),
            });
            return c;
        }

        static UIElement Empty(WidgetSize size)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Icon(Ui.G(0xE8D6), size == WidgetSize.Small ? 30 : 38, Soft);
            icon.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(icon);
            var a = Ui.Text("Nada en reproducción", 14, White, FontWeights.SemiBold);
            a.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(a);
            if (size != WidgetSize.Small)
            {
                var b = Ui.Text("Pon música en Spotify, YouTube…", 12, Soft);
                b.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(b);
            }
            return sp;
        }
    }
}
