using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace Tesela
{
    /// <summary>Muestreo compartido de CPU, GPU, memoria, disco y batería.</summary>
    static class SysInfo
    {
        /// <summary>Uso de GPU (0–1) o null si el equipo no expone los contadores.</summary>
        public static volatile object GpuBox;
        public static double? Gpu => GpuBox as double?;
        public static double GpuMemGb;

        static System.Threading.Timer gpuTimer;
        static readonly object gpuLock = new object();
        static PerformanceCounterCategory engineCat, memCat;
        static Dictionary<string, CounterSample> lastEngine = new Dictionary<string, CounterSample>();
        static bool gpuFailed;

        static void StartGpu()
        {
            if (gpuTimer != null || gpuFailed) return;
            gpuTimer = new System.Threading.Timer(_ => SampleGpu(), null, 0, 2000);
        }

        /// <summary>
        /// Igual que el Administrador de tareas: suma el uso de todos los procesos por motor de la GPU
        /// (3D, copia, vídeo, cómputo…) y se queda con el motor más ocupado. Se lee la categoría entera
        /// de una vez (barato) y el porcentaje sale de comparar dos lecturas seguidas. Va en un hilo aparte.
        /// </summary>
        static void SampleGpu()
        {
            if (!System.Threading.Monitor.TryEnter(gpuLock)) return;
            try
            {
                if (engineCat == null)
                {
                    engineCat = new PerformanceCounterCategory("GPU Engine");
                    memCat = new PerformanceCounterCategory("GPU Adapter Memory");
                }
                var now = new Dictionary<string, CounterSample>();
                var sums = new Dictionary<string, double>();
                var data = engineCat.ReadCategory()["Utilization Percentage"];
                if (data != null)
                    foreach (InstanceData d in data.Values)
                    {
                        now[d.InstanceName] = d.Sample;
                        if (!lastEngine.TryGetValue(d.InstanceName, out var prev)) continue;
                        float v;
                        try { v = CounterSample.Calculate(prev, d.Sample); } catch { continue; }
                        int i = d.InstanceName.IndexOf("luid", StringComparison.Ordinal);
                        string engine = i >= 0 ? d.InstanceName.Substring(i) : d.InstanceName;
                        sums.TryGetValue(engine, out var acc);
                        sums[engine] = acc + v;
                    }
                bool first = lastEngine.Count == 0;
                lastEngine = now;
                if (!first) GpuBox = Ui.Clamp((sums.Count > 0 ? sums.Values.Max() : 0) / 100, 0, 1);

                long mem = 0;
                var md = memCat.ReadCategory()["Dedicated Usage"];
                if (md != null)
                    foreach (InstanceData d in md.Values) mem = Math.Max(mem, d.RawValue);
                GpuMemGb = mem / 1073741824.0;
            }
            catch (Exception ex)
            {
                // Sin contadores de GPU (equipo antiguo o servicio desactivado): la métrica se oculta.
                gpuFailed = true;
                GpuBox = null;
                gpuTimer?.Dispose();
                Program.Log("gpu: " + ex.Message);
            }
            finally { System.Threading.Monitor.Exit(gpuLock); }
        }

        static long lastIdle, lastTotal;
        static DateTime lastSample;
        public static double Cpu, Ram;
        public static ulong RamTotal, RamUsed;
        public static double? Battery;
        public static bool Charging;
        public static double Disk;
        public static long DiskFree, DiskTotal;
        public static readonly List<double> History = new List<double>();

        public static void Sample()
        {
            StartGpu();
            if ((DateTime.Now - lastSample).TotalMilliseconds < 900) return;
            lastSample = DateTime.Now;

            if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
            {
                long total = kernel + user; // kernel ya incluye el tiempo idle
                long dIdle = idle - lastIdle, dTotal = total - lastTotal;
                if (lastTotal != 0 && dTotal > 0) Cpu = Ui.Clamp(1 - (double)dIdle / dTotal, 0, 1);
                lastIdle = idle;
                lastTotal = total;
            }
            History.Add(Cpu);
            if (History.Count > 60) History.RemoveAt(0);

            var mem = new Native.MEMORYSTATUSEX { dwLength = 64 };
            if (Native.GlobalMemoryStatusEx(ref mem))
            {
                RamTotal = mem.ullTotalPhys;
                RamUsed = mem.ullTotalPhys - mem.ullAvailPhys;
                Ram = (double)RamUsed / RamTotal;
            }

            var ps = Forms.SystemInformation.PowerStatus;
            Battery = ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ? (double?)null : ps.BatteryLifePercent;
            Charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;

            try
            {
                var d = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory));
                DiskTotal = d.TotalSize;
                DiskFree = d.TotalFreeSpace;
                Disk = 1 - (double)DiskFree / DiskTotal;
            }
            catch { }
        }
    }

    /// <summary>
    /// Monitor de hardware configurable. Cada métrica (uso, temperaturas, relojes, consumos, unidades, red,
    /// sensores…) se activa por separado, se ordena arrastrándola y se pinta con el estilo elegido: números,
    /// barras, anillos o gráficas históricas. Lo que el equipo no puede medir se muestra como no disponible.
    /// </summary>
    public class SystemWidget : Widget
    {
        public override string Kind => "system";
        public override TimeSpan? Interval => TimeSpan.FromSeconds(GetDouble("interval", 2));
        public override string DefaultAccentKey => "green";

        // ---- Catálogo de métricas -----------------------------------------------------------------

        class Def
        {
            public string Key, Name, Short, Group, Glyph;
            public Hw.Group Need;
            public bool Sensor, Panel;
        }

        static readonly Def[] Catalog =
        {
            new Def { Key = "cpu", Name = "Uso de CPU", Short = "CPU", Group = "cpu", Glyph = Ui.G(0xE950), Need = Hw.Group.Cpu },
            new Def { Key = "cpu.temp", Name = "Temperatura de la CPU", Short = "Temp. CPU", Group = "cpu", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Sensors, Sensor = true },
            new Def { Key = "cpu.freq", Name = "Frecuencia de la CPU", Short = "Frec. CPU", Group = "cpu", Glyph = Ui.G(0xEC4A), Need = Hw.Group.Cpu },
            new Def { Key = "cpu.power", Name = "Consumo de la CPU", Short = "Consumo CPU", Group = "cpu", Glyph = Ui.G(0xE945), Need = Hw.Group.Sensors, Sensor = true },
            new Def { Key = "cpu.cores", Name = "Uso por núcleo", Short = "Núcleos", Group = "cpu", Glyph = Ui.G(0xE950), Need = Hw.Group.Cores, Panel = true },
            new Def { Key = "gpu", Name = "Uso de GPU", Short = "GPU", Group = "gpu", Glyph = Ui.G(0xE7F4), Need = Hw.Group.Gpu },
            new Def { Key = "gpu.temp", Name = "Temperatura de la GPU", Short = "Temp. GPU", Group = "gpu", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Gpu },
            new Def { Key = "gpu.hotspot", Name = "Hotspot de la GPU", Short = "Hotspot", Group = "gpu", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Sensors, Sensor = true },
            new Def { Key = "gpu.vramtemp", Name = "Temperatura de la VRAM", Short = "Temp. VRAM", Group = "gpu", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Sensors, Sensor = true },
            new Def { Key = "gpu.clock", Name = "Relojes de la GPU", Short = "Reloj GPU", Group = "gpu", Glyph = Ui.G(0xEC4A), Need = Hw.Group.Gpu },
            new Def { Key = "gpu.power", Name = "Consumo de la GPU", Short = "Consumo GPU", Group = "gpu", Glyph = Ui.G(0xE945), Need = Hw.Group.Gpu },
            new Def { Key = "gpu.fan", Name = "Ventilador de la GPU", Short = "Ventilador", Group = "gpu", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Gpu },
            new Def { Key = "vram", Name = "Memoria de vídeo (VRAM)", Short = "VRAM", Group = "gpu", Glyph = Ui.G(0xE964), Need = Hw.Group.Gpu },
            new Def { Key = "ram", Name = "Memoria RAM", Short = "Memoria", Group = "ram", Glyph = Ui.G(0xE964), Need = Hw.Group.Ram },
            new Def { Key = "disk", Name = "Disco del sistema", Short = "Disco", Group = "disk", Glyph = Ui.G(0xEDA2), Need = Hw.Group.Disks },
            new Def { Key = "disk.io", Name = "Actividad de disco", Short = "Disco E/S", Group = "disk", Glyph = Ui.G(0xEDA2), Need = Hw.Group.DiskIo },
            new Def { Key = "net", Name = "Red (descarga y subida)", Short = "Red", Group = "net", Glyph = Ui.G(0xE968), Need = Hw.Group.Net },
            new Def { Key = "net.session", Name = "Tráfico de la sesión", Short = "Sesión", Group = "net", Glyph = Ui.G(0xE968), Need = Hw.Group.Net },
            new Def { Key = "battery", Name = "Batería", Short = "Batería", Group = "battery", Glyph = Ui.G(0xE83F) },
            new Def { Key = "board", Name = "Temperaturas de la placa base", Short = "Placa", Group = "sensors", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Sensors, Sensor = true, Panel = true },
            new Def { Key = "fans", Name = "Ventiladores del sistema", Short = "Ventiladores", Group = "sensors", Glyph = Ui.G(0xE9CA), Need = Hw.Group.Sensors, Sensor = true, Panel = true },
            new Def { Key = "volts", Name = "Voltajes", Short = "Voltajes", Group = "sensors", Glyph = Ui.G(0xE945), Need = Hw.Group.Sensors, Sensor = true, Panel = true },
        };

        /// <summary>El catálogo más una métrica por cada unidad fija que no sea la del sistema.</summary>
        List<Def> AllDefs()
        {
            var list = Catalog.ToList();
            var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            int at = list.FindIndex(d => d.Key == "disk") + 1;
            foreach (var d in Hw.Now.Drives.Where(d => !d.Root.Equals(sys, StringComparison.OrdinalIgnoreCase)))
                list.Insert(at++, new Def
                {
                    Key = "disk:" + d.Root, Name = L.F("Unidad {0} ({1})", d.Root.TrimEnd('\\'), d.Label), Short = d.Root.TrimEnd('\\'),
                    Group = "disk", Glyph = Ui.G(0xEDA2), Need = Hw.Group.Disks,
                });
            return list;
        }

        static Def Find(string key) => Catalog.FirstOrDefault(d => d.Key == key)
            ?? (key.StartsWith("disk:") ? new Def { Key = key, Name = L.T("Unidad ") + key.Substring(5).TrimEnd('\\'), Short = key.Substring(5).TrimEnd('\\'),
                Group = "disk", Glyph = Ui.G(0xEDA2), Need = Hw.Group.Disks } : null);

        /// <summary>Orden de todas las métricas (las nuevas se añaden al final).</summary>
        List<string> Order()
        {
            var saved = (Get("order", "") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var d in AllDefs()) if (!saved.Contains(d.Key)) saved.Add(d.Key);
            return saved.Where(k => Find(k) != null).ToList();
        }

        HashSet<string> Enabled()
        {
            var on = Get("on", null);
            if (on != null) return new HashSet<string>(on.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            // Ajustes de versiones anteriores
            var set = new HashSet<string>();
            foreach (var (key, opt) in new[] { ("cpu", "showCpu"), ("gpu", "showGpu"), ("ram", "showRam"), ("disk", "showDisk"), ("battery", "showBattery") })
                if (GetBool(opt, true)) set.Add(key);
            return set;
        }

        List<string> Visible() => Order().Where(Enabled().Contains).ToList();

        // ---- Ajustes ------------------------------------------------------------------------------

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Métricas");
            var on = Enabled();
            c.OrderList(Order().Select(k => Find(k)).Where(d => d != null).Select(d => (d.Key, d.Name, Availability(d), on.Contains(d.Key))),
                (order, enabled) =>
                {
                    Set("order", string.Join(",", order));
                    Set("on", string.Join(",", order.Where(enabled.Contains)));
                });
            c.Hint("Arrastra para ordenar.");

            // Sensores integrados: temperaturas de CPU y placa, ventiladores y voltajes sin LibreHardwareMonitor
            c.Section("Sensores");
            TextBlock state = null;
            string Describe() => Hw.SensorSource == "Tesela"
                ? "Activos: Tesela lee los sensores por sí solo."
                : Sensors.Enabled
                    ? "Activados. Arrancando la lectura de sensores…"
                    : Hw.SensorSource == "LibreHardwareMonitor"
                        ? "Ahora se leen de LibreHardwareMonitor. Actívalos para no tener que tenerlo abierto."
                        : "Las temperaturas de CPU y placa, los ventiladores y los voltajes necesitan permisos de administrador.";
            if (Sensors.Bundled)
            {
                c.Custom("Sensores integrados", c.Button(Sensors.Enabled ? "Desactivar" : "Activar", () =>
                {
                    if (Sensors.Enabled) Sensors.Disable(); else Sensors.Enable();
                    c.Changed();
                    state.Text = L.T(Describe());
                }));
            }
            state = c.Hint(L.T(Describe()) + (Sensors.Bundled && !Sensors.IsElevated && !Sensors.Enabled
                ? L.T(" Al activarlos, Windows pide permiso una sola vez y se crea la tarea programada «Tesela\\Sensores».")
                : ""));

            c.Section("Presentación");
            c.Choice("Estilo", "style", new[] { ("numbers", "Números"), ("bars", "Barras"), ("rings", "Anillos"), ("chart", "Gráficas") }, "rings");
            c.Choice("Densidad", "density", new[] { ("compact", "Compacta"), ("normal", "Normal"), ("comfy", "Amplia") }, "normal");
            c.Slider("Tamaño del texto", "textScale", 85, 125, 100, v => $"{v:0} %");
            c.Slider("Grosor de los anillos", "thickness", 4, 14, 8, v => $"{v:0} px");
            c.Toggle("Valor dentro del anillo", "ringText", true);

            // Las métricas de la lista y, aunque no estén activadas en ella, las principales (uso de CPU y GPU,
            // memoria y temperaturas)
            var names = Visible().Concat(new[] { "cpu", "gpu", "ram", "cpu.temp", "gpu.temp" }).Distinct()
                .Select(k => Find(k)).Where(d => d != null).ToList();
            if (names.Count > 0)
            {
                c.Section("Tamaño pequeño");
                c.Combo("Métrica principal", "metricName", names.Select(d => d.Name).ToArray(),
                    Find(Get("metric", "cpu"))?.Name ?? names[0].Name);
            }

            c.Section("Tamaño grande");
            c.Toggle("Gráfica histórica", "chart", true);
            if (names.Count > 0)
                c.Combo("Métrica de la gráfica", "chartMetricName", new[] { FirstMetric }.Concat(names.Select(d => d.Name)).ToArray(),
                    Find(Get("chartMetric", ""))?.Name ?? FirstMetric);
            c.Toggle("Tiempo encendido", "uptime", true);
            c.Toggle("Adaptador e IP local en la red", "netIp", false);

            c.Section("Unidades");
            c.Choice("Temperatura", "tempUnit", new[] { ("c", "°C"), ("f", "°F") }, "c");
            c.Choice("Velocidad de red", "netUnit", new[] { ("bytes", "MB/s"), ("bits", "Mbit/s") }, "bytes");
            c.Choice("Frecuencia", "freqUnit", new[] { ("ghz", "GHz"), ("mhz", "MHz") }, "ghz");

            c.Section("Actualización");
            c.Choice("Actualizar cada", "interval", new[] { ("1", "1 s"), ("2", "2 s"), ("5", "5 s") }, "2",
                () => Host?.ApplySize());
        }

        static readonly (string Group, string Title, string Desc)[] ColorGroups =
        {
            ("cpu", "Procesador (CPU)", "Uso, temperatura, frecuencia, consumo y núcleos"),
            ("gpu", "Tarjeta gráfica (GPU)", "Uso, temperaturas, relojes, consumo, ventilador y VRAM"),
            ("ram", "Memoria (RAM)", "Uso de la memoria"),
            ("disk", "Almacenamiento", "Unidades y actividad de disco"),
            ("net", "Red", "Descarga, subida y tráfico"),
            ("sensors", "Sensores", "Placa base, ventiladores y voltajes"),
            ("battery", "Batería", "Carga de la batería"),
        };

        /// <summary>Color predeterminado de cada componente (el de "Automático").</summary>
        static Color DefaultColor(string group, Theme t)
        {
            switch (group)
            {
                case "gpu": return Theme.AccentColor("purple", t.Dark);
                case "ram": return Theme.AccentColor("blue", t.Dark);
                case "disk": return Theme.AccentColor("orange", t.Dark);
                case "net": return Theme.AccentColor("teal", t.Dark);
                case "sensors": return Theme.AccentColor("red", t.Dark);
                case "battery": return Theme.AccentColor("green", t.Dark);
                default: return ((SolidColorBrush)t.Accent).Color;
            }
        }

        /// <summary>
        /// Pestaña Colores: el modo (un color por componente o todo con el acento) y una subsección por
        /// componente con las mismas muestras y el mismo selector RGB que el resto de la app.
        /// </summary>
        public override void BuildColors(SettingsContext c)
        {
            var t = Host?.CurrentTheme ?? Theme.Create(true);
            c.Section("Colores de las métricas");
            c.Choice("Modo", "colors", new[] { ("multi", "Un color por componente"), ("accent", "Todo con el acento") }, "multi");
            c.Hint("Automático: el color predeterminado de cada componente (en la CPU, el acento del widget).");
            foreach (var (g, title, desc) in ColorGroups)
            {
                if (g == "battery" && SysInfo.Battery == null) continue;
                c.Section(title);
                var key = "color." + g;
                var group = g;
                var auto = DefaultColor(g, t);
                var swatches = new List<(string, string, Brush)> { ("auto", "Automático", new SolidColorBrush(auto)) };
                swatches.AddRange(Theme.Accents.Select(a => (a.Key, a.Name, (Brush)Ui.Hex(t.Dark ? a.Dark : a.Light))));
                c.Colors("Color", swatches, Get(key, "auto"), v =>
                {
                    Set(key, v);
                    c.Changed();
                });
                c.Hint(desc);
            }
        }

        const string FirstMetric = "La primera de la lista";

        public override void Set(string key, string value)
        {
            // La métrica principal se elige por nombre en el desplegable
            if (key == "metricName")
            {
                var d = AllDefs().FirstOrDefault(x => x.Name == value);
                if (d != null) base.Set("metric", d.Key);
                return;
            }
            // La métrica de la gráfica, igual (o la primera de la lista, como antes)
            if (key == "chartMetricName")
            {
                var d = value == FirstMetric ? null : AllDefs().FirstOrDefault(x => x.Name == value);
                if (d != null) base.Set("chartMetric", d.Key); else State.Data.Remove("chartMetric");
                return;
            }
            base.Set(key, value);
        }

        /// <summary>Por qué una métrica no se puede mostrar (o null si está disponible).</summary>
        static string Availability(Def d)
        {
            var s = Hw.Now;
            if (d.Sensor && !s.SensorsProvider) return "Requiere los sensores integrados (Contenido → Sensores)";
            if (Hw.Count < 2) return null;
            switch (d.Key)
            {
                case "gpu.temp": return s.GpuTemp == null ? "No disponible en esta GPU" : null;
                case "gpu.power": return s.GpuPower == null ? "No disponible en esta GPU" : null;
                case "gpu.clock": return s.GpuCoreMhz == null ? "No disponible en esta GPU" : null;
                case "gpu.fan": return s.GpuFan == null ? "No disponible en esta GPU" : null;
                case "battery": return SysInfo.Battery == null ? "Este equipo no tiene batería" : null;
            }
            return null;
        }

        // ---- Valores ------------------------------------------------------------------------------

        class Metric
        {
            public Def Def;
            public string Value, Foot;
            public double? V;          // 0–1 para anillos y barras
            public Brush Color;
            public bool Available = true;
            public List<(string Name, string Value)> Lines;  // métricas de panel (núcleos, sensores)
            public double[] Bars;
        }

        readonly Dictionary<string, double> maxSeen = new Dictionary<string, double>();
        // Métrica que dibuja la gráfica histórica del tamaño grande
        Metric chartMetric;
        readonly Dictionary<string, List<double>> history = new Dictionary<string, List<double>>();
        readonly Dictionary<string, DateTime> historyAt = new Dictionary<string, DateTime>();

        double Rel(string key, double v, double floor)
        {
            maxSeen.TryGetValue(key, out var m);
            m = Math.Max(Math.Max(m, v), floor);
            maxSeen[key] = m;
            return v / m;
        }

        void Record(string key, double? v)
        {
            if (!v.HasValue) return;
            // Un punto por intervalo (al cambiar ajustes también se repinta, sin añadir puntos)
            if (historyAt.TryGetValue(key, out var at) && (DateTime.Now - at).TotalSeconds < GetDouble("interval", 2) * 0.8) return;
            historyAt[key] = DateTime.Now;
            if (!history.TryGetValue(key, out var h)) history[key] = h = new List<double>();
            h.Add(Ui.Clamp(v.Value, 0, 1));
            if (h.Count > 60) h.RemoveAt(0);
        }

        string Temp(double c) => Get("tempUnit", "c") == "f" ? $"{Math.Round(c * 9 / 5 + 32)} °F" : $"{Math.Round(c)} °C";
        string Freq(double mhz) => Get("freqUnit", "ghz") == "mhz" ? $"{Math.Round(mhz)} MHz" : $"{mhz / 1000:0.00} GHz";
        string Rate(double bytesPerSec)
        {
            if (Get("netUnit", "bytes") == "bits")
            {
                double mbit = bytesPerSec * 8 / 1e6;
                return mbit >= 100 ? $"{mbit:0} Mbit/s" : mbit >= 1 ? $"{mbit:0.0} Mbit/s" : $"{bytesPerSec * 8 / 1e3:0} kbit/s";
            }
            double mb = bytesPerSec / 1048576;
            return mb >= 100 ? $"{mb:0} MB/s" : mb >= 1 ? $"{mb:0.0} MB/s" : $"{bytesPerSec / 1024:0} KB/s";
        }
        static string Size(double bytes) => bytes >= 1099511627776 ? $"{bytes / 1099511627776:0.0} TB" : bytes >= 1073741824 ? $"{bytes / 1073741824:0.#} GB" : $"{bytes / 1048576:0} MB";
        static string Pct(double v) => $"{Math.Round(v * 100)} %";
        static string ShortGpu(string n) => (n ?? "").Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("Radeon ", "Radeon ").Trim();
        static string ShortCpu(string n) => (n ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("Processor", "").Replace(" CPU", "")
            .Replace("-Core", "").Replace("  ", " ").Trim();

        Brush ColorOf(string group, Theme t)
        {
            if (Get("colors", "multi") != "multi") return t.Accent;
            var c = Get("color." + group, "auto");
            if (c != "auto") return new SolidColorBrush(Theme.AccentColor(c, t.Dark));
            if (group == "battery" && SysInfo.Battery < 0.2 && !SysInfo.Charging) return t.Red;
            return new SolidColorBrush(DefaultColor(group, t));
        }

        Metric Make(Def d, Theme t)
        {
            var s = Hw.Now;
            var m = new Metric { Def = d, Color = ColorOf(d.Group, t) };
            bool waiting = Hw.Count < 2;
            void Missing(string why = null)
            {
                m.Available = false;
                m.Value = "—";
                m.V = null;
                m.Foot = d.Sensor && !s.SensorsProvider ? "Requiere sensores" : waiting ? "Midiendo…" : why ?? "No disponible";
            }
            switch (d.Key)
            {
                case "cpu":
                    m.V = SysInfo.Cpu;
                    m.Value = Pct(SysInfo.Cpu);
                    m.Foot = s.CpuMhz.HasValue ? Freq(s.CpuMhz.Value) : ShortCpu(s.CpuName);
                    break;
                case "cpu.temp":
                    if (s.CpuTemp is double ct) { m.Value = Temp(ct); m.V = ct / 100; m.Foot = "Procesador"; } else Missing();
                    break;
                case "cpu.freq":
                    if (s.CpuMhz is double mhz) { m.Value = Freq(mhz); m.V = Rel(d.Key, mhz, 1000); m.Foot = L.T("Máx. ") + Freq(s.CpuMaxMhz ?? mhz); } else Missing();
                    break;
                case "cpu.power":
                    if (s.CpuPower is double cw) { m.Value = $"{cw:0} W"; m.V = Rel(d.Key, cw, 65); m.Foot = "Paquete"; } else Missing();
                    break;
                case "cpu.cores":
                    if (s.Cores != null && s.Cores.Length > 0)
                    {
                        m.Bars = s.Cores;
                        m.V = s.Cores.Average();
                        m.Value = Pct(m.V.Value);
                        m.Foot = L.F("{0} hilos", s.Cores.Length);
                    }
                    else Missing();
                    break;
                case "gpu":
                    if (s.GpuUsage is double gu) { m.V = gu; m.Value = Pct(gu); m.Foot = ShortGpu(s.GpuName); }
                    else if (SysInfo.Gpu is double g2) { m.V = g2; m.Value = Pct(g2); m.Foot = ShortGpu(s.GpuName); }
                    else Missing();
                    break;
                case "gpu.temp":
                    if (s.GpuTemp is double gt) { m.Value = Temp(gt); m.V = gt / 100; m.Foot = ShortGpu(s.GpuName); } else Missing();
                    break;
                case "gpu.hotspot":
                    if (s.GpuHotspot is double hs) { m.Value = Temp(hs); m.V = hs / 110; m.Foot = "Punto más caliente"; } else Missing();
                    break;
                case "gpu.vramtemp":
                    if (s.GpuVramTemp is double vt) { m.Value = Temp(vt); m.V = vt / 110; m.Foot = "Memoria gráfica"; } else Missing();
                    break;
                case "gpu.clock":
                    if (s.GpuCoreMhz is double gc)
                    {
                        m.Value = $"{gc:0} MHz";
                        m.V = Rel(d.Key, gc, 1000);
                        m.Foot = s.GpuMemMhz.HasValue ? L.F("Memoria {0:0} MHz", s.GpuMemMhz) : "Núcleo";
                    }
                    else Missing();
                    break;
                case "gpu.power":
                    if (s.GpuPower is double gw) { m.Value = $"{gw:0} W"; m.V = Rel(d.Key, gw, 100); m.Foot = "Tarjeta gráfica"; } else Missing();
                    break;
                case "gpu.fan":
                    if (s.GpuFan is double gf) { m.Value = Pct(gf); m.V = gf; m.Foot = gf < 0.005 ? "Parado (modo silencio)" : "Velocidad"; } else Missing();
                    break;
                case "vram":
                    if (s.VramUsedGb is double vu)
                    {
                        if (s.VramTotalGb is double vtot && vtot > 0)
                        {
                            m.V = vu / vtot;
                            m.Value = Pct(m.V.Value);
                            m.Foot = L.F("{0:0.0} de {1:0} GB", vu, vtot);
                        }
                        else { m.Value = $"{vu:0.0} GB"; m.Foot = "En uso"; }
                    }
                    else if (SysInfo.GpuMemGb > 0) { m.Value = $"{SysInfo.GpuMemGb:0.0} GB"; m.Foot = "En uso"; }
                    else Missing();
                    break;
                case "ram":
                    m.V = SysInfo.Ram;
                    m.Value = Pct(SysInfo.Ram);
                    m.Foot = L.F("{0} de {1}", Size(SysInfo.RamUsed), Size(SysInfo.RamTotal)) + (s.RamMts.HasValue ? $" · {s.RamMts:0} MT/s" : "");
                    break;
                case "disk":
                {
                    var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                    var dr = s.Drives.FirstOrDefault(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
                    if (dr.Root == null) { dr = (root, "", SysInfo.DiskTotal, SysInfo.DiskFree); }
                    if (dr.Total > 0)
                    {
                        m.V = 1 - (double)dr.Free / dr.Total;
                        m.Value = Pct(m.V.Value);
                        m.Foot = L.F("{0} libres de {1}", Size(dr.Free), Size(dr.Total));
                    }
                    else Missing();
                    break;
                }
                case "disk.io":
                    if (s.DiskActive is double da)
                    {
                        m.V = da;
                        m.Value = Pct(da);
                        m.Foot = L.F("L {0} · E {1}", Rate(s.DiskRead ?? 0), Rate(s.DiskWrite ?? 0));
                    }
                    else Missing();
                    break;
                case "net":
                    if (s.NetDown is double down)
                    {
                        m.Value = "↓ " + Rate(down);
                        m.V = Rel(d.Key, down, 1048576);
                        m.Foot = "↑ " + Rate(s.NetUp ?? 0);
                        if (GetBool("netIp", false) && s.NetIp != null) m.Foot += " · " + s.NetIp;
                    }
                    else Missing(s.NetName == null ? "Sin conexión" : null);
                    break;
                case "net.session":
                    if (Hw.Count >= 2) { m.Value = "↓ " + Size(s.NetSessionDown); m.Foot = "↑ " + Size(s.NetSessionUp); }
                    else Missing();
                    break;
                case "battery":
                    if (SysInfo.Battery is double b)
                    {
                        m.V = b;
                        m.Value = Pct(b);
                        m.Foot = SysInfo.Charging ? "Cargando" : "Con batería";
                    }
                    else Missing("Sin batería");
                    break;
                case "board":
                    if (s.BoardTemps.Count > 0)
                    {
                        var mx = s.BoardTemps.Max(x => x.Value);
                        m.Value = Temp(mx);
                        m.V = mx / 100;
                        m.Foot = L.F("{0} sensores", s.BoardTemps.Count);
                        m.Lines = s.BoardTemps.Select(x => (x.Name, Temp(x.Value))).ToList();
                    }
                    else Missing();
                    break;
                case "fans":
                    if (s.Fans.Count > 0)
                    {
                        var mx = s.Fans.Max(x => x.Value);
                        m.Value = $"{mx:0} RPM";
                        m.V = Rel(d.Key, mx, 1500);
                        m.Foot = L.F("{0} ventiladores", s.Fans.Count);
                        m.Lines = s.Fans.Select(x => (x.Name, $"{x.Value:0} RPM")).ToList();
                    }
                    else Missing();
                    break;
                case "volts":
                    if (s.Volts.Count > 0)
                    {
                        m.Value = $"{s.Volts[0].Value:0.000} V";
                        m.Foot = s.Volts[0].Name;
                        m.Lines = s.Volts.Select(x => (x.Name, $"{x.Value:0.000} V")).ToList();
                    }
                    else Missing();
                    break;
                default:
                    if (d.Key.StartsWith("disk:"))
                    {
                        var dr = s.Drives.FirstOrDefault(x => x.Root.Equals(d.Key.Substring(5), StringComparison.OrdinalIgnoreCase));
                        if (dr.Root != null && dr.Total > 0)
                        {
                            m.V = 1 - (double)dr.Free / dr.Total;
                            m.Value = Pct(m.V.Value);
                            m.Foot = L.F("{0} libres de {1}", Size(dr.Free), Size(dr.Total));
                        }
                        else Missing();
                    }
                    break;
            }
            Record(d.Key, m.V);
            return m;
        }

        // ---- Pintado ------------------------------------------------------------------------------

        double TS => GetDouble("textScale", 100) / 100;
        double Thick => GetDouble("thickness", 8);
        bool RingText => GetBool("ringText", true);
        string Style => Get("style", "rings");
        double Gap => Get("density", "normal") == "compact" ? 4 : Get("density", "normal") == "comfy" ? 12 : 8;

        public override UIElement Render(Theme t, WidgetSize size)
        {
            SysInfo.Sample();
            var keys = Visible();
            // En pequeño solo se ve la métrica principal, que puede no estar entre las de la lista
            if (size == WidgetSize.Small && Find(Get("metric", "cpu")) != null) keys = new List<string> { Get("metric", "cpu") };
            var defs = keys.Select(Find).Where(d => d != null).ToList();
            var need = Hw.Group.Cpu | Hw.Group.Ram;
            foreach (var d in defs) need |= d.Need;
            if (defs.Count == 0) defs.Add(Find("cpu"));
            // La métrica de la gráfica del tamaño grande puede no estar entre las de la lista: se mide igual
            // (para ir guardando su historia) pero no se muestra como una más
            var chartDef = Find(Get("chartMetric", "")) ?? defs[0];
            if (!defs.Any(d => d.Key == chartDef.Key)) need |= chartDef.Need;
            Hw.Need(need, Interval ?? TimeSpan.FromSeconds(2));
            var ms = defs.Select(d => Make(d, t)).ToList();
            chartMetric = ms.FirstOrDefault(m => m.Def.Key == chartDef.Key) ?? Make(chartDef, t);

            switch (size)
            {
                case WidgetSize.Small:
                {
                    var main = ms.FirstOrDefault(m => m.Def.Key == Get("metric", "cpu")) ?? ms[0];
                    return RenderSmall(t, main);
                }
                case WidgetSize.Medium: return RenderMedium(t, ms);
                default: return RenderLarge(t, ms);
            }
        }

        TextBlock Txt(string s, double size, Brush b, FontWeight w) => Ui.Text(s, Math.Round(size * TS, 1), b, w);

        UIElement RenderSmall(Theme t, Metric m)
        {
            var g = Ui.Rows(Ui.Auto, Ui.Star(), Ui.Auto);
            g.Children.Add(Ui.At(Header(m.Def.Short, m.Def.Glyph, m.Color, t), 0));
            FrameworkElement body;
            bool center = Style == "rings";
            if (m.Bars != null) body = CoreBars(m, t, 64);
            else if (m.Lines != null) body = Lines(m, t, 4);
            else if (Style == "rings") body = Ring(88, m.V ?? 0, m.Available ? m.Color : t.Track, t, RingText ? m.Value : "", Thick * 1.1, TS);
            else if (Style == "chart")
            {
                var sp = new StackPanel();
                sp.Children.Add(Big(m, t, 30));
                var ch = Chart(m, t);
                ch.Height = 44;
                ch.Margin = new Thickness(0, 6, 0, 0);
                sp.Children.Add(ch);
                body = sp;
            }
            else
            {
                var sp = new StackPanel();
                sp.Children.Add(Big(m, t, 36));
                if (Style == "bars") sp.Children.Add(BarOnly(m.V ?? 0, m.Color, t, 8));
                body = sp;
            }
            body.HorizontalAlignment = center && m.Bars == null && m.Lines == null ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            body.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(body, 1));
            var foot = Txt(m.Foot, 12, t.Secondary, FontWeights.Medium);
            foot.HorizontalAlignment = center && m.Bars == null && m.Lines == null ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            g.Children.Add(Ui.At(foot, 2));
            return g;
        }

        UIElement RenderMedium(Theme t, List<Metric> ms)
        {
            // Las métricas de panel (núcleos, sensores) se resumen en una celda normal en este tamaño
            switch (Style)
            {
                case "bars":
                {
                    int n = Gap <= 4 ? 4 : Gap >= 12 ? 2 : 3;
                    var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    foreach (var m in ms.Take(n)) rows.Children.Add(BarRow(m, t, Gap * 0.6));
                    return rows;
                }
                case "chart":
                {
                    var g = new Grid();
                    int n = Math.Min(2, ms.Count);
                    for (int i = 0; i < n; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
                    for (int i = 0; i < n; i++)
                    {
                        var cell = ChartCell(ms[i], t);
                        cell.Margin = new Thickness(i == 0 ? 0 : Gap, 0, i == n - 1 ? 0 : Gap, 0);
                        g.Children.Add(Ui.At(cell, 0, i));
                    }
                    return g;
                }
                default:
                {
                    int max = Gap >= 12 ? 3 : 4;
                    int n = Math.Min(max, ms.Count);
                    var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
                    for (int i = 0; i < n; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
                    double ring = Math.Min(78, (WidgetHost.Unit * 2 + WidgetHost.Gap - 32) / n - 12 - Gap);
                    for (int i = 0; i < n; i++)
                    {
                        var m = ms[i];
                        FrameworkElement cell = Style == "rings" ? Gauge(m, ring, t) : NumberCell(m, t, n > 3 ? 22 : 26, true);
                        g.Children.Add(Ui.At(cell, 0, i));
                    }
                    return g;
                }
            }
        }

        UIElement RenderLarge(Theme t, List<Metric> ms)
        {
            // Se mide de verdad lo que cabe: se van añadiendo métricas mientras quepan en el widget.
            var inner = WidgetHost.Dimensions(WidgetSize.Large);
            var pad = Padding(WidgetSize.Large);
            double w = inner.Width - pad.Left - pad.Right, h = inner.Height - pad.Top - pad.Bottom;

            var top = new StackPanel();
            var head = Ui.Cols(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.At(Txt("Sistema", 17, t.Text, FontWeights.Bold), 0, 0));
            if (GetBool("uptime", true))
            {
                var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
                var upText = Txt(L.F("Encendido {0}d {1}h {2}m", (int)up.TotalDays, up.Hours, up.Minutes), 12, t.Secondary, FontWeights.Normal);
                upText.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(Ui.At(upText, 0, 1));
            }
            top.Children.Add(head);
            if (GetBool("chart", true) && Style != "chart" && ms.Count > 0)
            {
                var ch = Chart(chartMetric ?? ms[0], t);
                ch.Height = 64;
                ch.Margin = new Thickness(0, Gap, 0, 0);
                top.Children.Add(ch);
            }
            top.Measure(new Size(w, double.PositiveInfinity));
            double room = h - top.DesiredSize.Height - Gap;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bool Fits()
            {
                body.Measure(new Size(w, double.PositiveInfinity));
                return body.DesiredSize.Height <= room;
            }
            int cols = Style == "bars" ? 1 : 2;
            Grid grid = null;
            int col = 0;
            foreach (var m in ms)
            {
                bool asCell = false;
            retry:
                if ((m.Bars != null || m.Lines != null) && !asCell)
                {
                    StackPanel panel = new StackPanel { Margin = new Thickness(0, Gap * 0.6, 0, Gap * 0.6) };
                    var ph = Ui.Cols(Ui.Star(), Ui.Auto);
                    ph.Children.Add(Ui.At(Txt(m.Def.Short, 13, t.Text, FontWeights.SemiBold), 0, 0));
                    ph.Children.Add(Ui.At(Txt(m.Available ? m.Value : m.Foot, 12, t.Secondary, FontWeights.Medium), 0, 1));
                    panel.Children.Add(ph);
                    if (m.Available)
                    {
                        var pb = m.Bars != null ? CoreBars(m, t, 40) : Lines(m, t, 5);
                        pb.Margin = new Thickness(0, 6, 0, 0);
                        panel.Children.Add(pb);
                    }
                    body.Children.Add(panel);
                    if (Fits()) { grid = null; col = 0; continue; }
                    body.Children.Remove(panel);
                    // La lista no cabe entera: se prueba con menos líneas y, si tampoco, como una celda normal
                    if (m.Lines != null && m.Available && m.Lines.Count > 2)
                    {
                        panel.Children.RemoveAt(panel.Children.Count - 1);
                        var shortList = Lines(m, t, 2);
                        shortList.Margin = new Thickness(0, 6, 0, 0);
                        panel.Children.Add(shortList);
                        body.Children.Add(panel);
                        if (Fits()) { grid = null; col = 0; continue; }
                        body.Children.Remove(panel);
                    }
                    asCell = true;
                    goto retry;
                }
                FrameworkElement cell;
                switch (Style)
                {
                    case "bars": cell = BarRow(m, t, Gap * 0.5); break;
                    case "chart": cell = ChartCell(m, t); break;
                    case "rings": cell = RingRow(m, t); break;
                    default: cell = NumberCell(m, t, 26, false); break;
                }
                bool newRow = grid == null || col >= cols;
                if (newRow)
                {
                    grid = new Grid();
                    for (int i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
                    body.Children.Add(grid);
                    col = 0;
                }
                cell.Margin = new Thickness(col == 0 ? 0 : Gap, Gap / 2, col == 0 && cols > 1 ? Gap : 0, Gap / 2);
                grid.Children.Add(Ui.At(cell, 0, col++));
                if (!Fits())
                {
                    grid.Children.Remove(cell);
                    if (grid.Children.Count == 0) body.Children.Remove(grid);
                    break;
                }
            }

            var g = Ui.Rows(Ui.Auto, Ui.Star());
            g.Children.Add(Ui.At(top, 0));
            body.Margin = new Thickness(0, Gap, 0, 0);
            g.Children.Add(Ui.At(body, 1));
            return g;
        }

        // ---- Piezas -----------------------------------------------------------------------------

        FrameworkElement Big(Metric m, Theme t, double fs)
        {
            var tb = new TextBlock { FontFamily = Ui.For(Math.Round(fs * TS)), Foreground = m.Available ? t.Text : t.Secondary, TextTrimming = TextTrimming.CharacterEllipsis };
            var v = m.Value ?? "";
            // El número grande y la unidad pequeña y en el color del componente, como en iOS
            int i = v.LastIndexOf(' ');
            bool split = i > 0 && !v.StartsWith("↓") && !v.StartsWith("↑");
            tb.Inlines.Add(new System.Windows.Documents.Run(split ? v.Substring(0, i) : v) { FontSize = Math.Round(fs * TS), FontWeight = FontWeights.SemiBold });
            if (split)
                tb.Inlines.Add(new System.Windows.Documents.Run(" " + v.Substring(i + 1)) { FontSize = Math.Round(fs * 0.45 * TS), FontWeight = FontWeights.SemiBold, Foreground = m.Color });
            return tb;
        }

        FrameworkElement NumberCell(Metric m, Theme t, double fs, bool center)
        {
            var sp = new StackPanel { HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            var l = Txt(m.Def.Short, 12, t.Secondary, FontWeights.Medium);
            var n = Big(m, t, fs);
            var f = Txt(m.Foot, 11, t.Secondary, FontWeights.Normal);
            if (center) { l.HorizontalAlignment = n.HorizontalAlignment = f.HorizontalAlignment = HorizontalAlignment.Center; }
            if (center) { sp.Children.Add(n); sp.Children.Add(l); }
            else
            {
                sp.Children.Add(l);
                sp.Children.Add(n);
                if (m.V.HasValue) sp.Children.Add(BarOnly(m.V.Value, m.Color, t, 4));
                else sp.Children.Add(f);
            }
            return sp;
        }

        FrameworkElement BarRow(Metric m, Theme t, double gap)
        {
            var g = Ui.Rows(Ui.Auto, Ui.Px(6));
            g.Margin = new Thickness(0, gap, 0, gap);
            var top = Ui.Cols(Ui.Star(), Ui.Auto);
            top.Margin = new Thickness(0, 0, 0, 4);
            top.Children.Add(Ui.At(Txt(m.Def.Short, 13, t.Text, FontWeights.SemiBold), 0, 0));
            var val = Txt(m.Available ? m.Value + (m.Def.Key == "net" ? "  " + m.Foot : "") : m.Foot, 12, t.Secondary, FontWeights.Medium);
            top.Children.Add(Ui.At(val, 0, 1));
            g.Children.Add(Ui.At(top, 0));
            g.Children.Add(Ui.At(BarOnly(m.V ?? 0, m.Color, t, 6, 0), 1));
            return g;
        }

        FrameworkElement RingRow(Metric m, Theme t)
        {
            var row = new DockPanel();
            var ring = Ring(52, m.V ?? 0, m.Available ? m.Color : t.Track, t, RingText ? ShortValue(m) : "", Thick * 0.75, TS);
            DockPanel.SetDock(ring, Dock.Left);
            row.Children.Add(ring);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            texts.Children.Add(Txt(m.Def.Short, 13, t.Text, FontWeights.SemiBold));
            texts.Children.Add(Txt(RingText ? m.Foot : m.Value, 11, t.Secondary, FontWeights.Normal));
            row.Children.Add(texts);
            return row;
        }

        FrameworkElement Gauge(Metric m, double size, Theme t)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Ring(size, m.V ?? 0, m.Available ? m.Color : t.Track, t, RingText ? ShortValue(m) : "", Thick, TS));
            var l = Txt(m.Def.Short, 12, t.Secondary, FontWeights.Medium);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, Gap, 0, 0);
            sp.Children.Add(l);
            return sp;
        }

        /// <summary>Valor corto para dentro de un anillo (sin flechas ni decimales sobrantes).</summary>
        static string ShortValue(Metric m)
        {
            var v = m.Value ?? "";
            if (v.StartsWith("↓ ")) v = v.Substring(2);
            return v.Replace(" GHz", "G").Replace(" MHz", "M").Replace(" RPM", "").Replace(" Mbit/s", "M").Replace(" MB/s", "M").Replace(" KB/s", "K");
        }

        FrameworkElement ChartCell(Metric m, Theme t)
        {
            var sp = new Grid();
            sp.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sp.RowDefinitions.Add(new RowDefinition());
            var top = Ui.Cols(Ui.Star(), Ui.Auto);
            top.Children.Add(Ui.At(Txt(m.Def.Short, 12, t.Secondary, FontWeights.Medium), 0, 0));
            top.Children.Add(Ui.At(Txt(m.Value, 13, m.Available ? t.Text : t.Secondary, FontWeights.SemiBold), 0, 1));
            sp.Children.Add(top);
            var ch = Chart(m, t);
            ch.MinHeight = 40;
            ch.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(Ui.At(ch, 1));
            return sp;
        }

        FrameworkElement Chart(Metric m, Theme t)
        {
            history.TryGetValue(m.Def.Key, out var data);
            return Sparkline(data ?? new List<double>(), m.Color, t);
        }

        /// <summary>Uso de cada hilo como columnas finas, de abajo arriba.</summary>
        FrameworkElement CoreBars(Metric m, Theme t, double h)
        {
            var g = new Grid { Height = h };
            foreach (var _ in m.Bars) g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < m.Bars.Length; i++)
            {
                var col = new Grid { Margin = new Thickness(m.Bars.Length > 16 ? 1 : 2, 0, m.Bars.Length > 16 ? 1 : 2, 0) };
                col.Children.Add(new Border { Background = t.Track, CornerRadius = new CornerRadius(2) });
                col.Children.Add(new Border
                {
                    Background = m.Color, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom,
                    Height = Math.Max(2, m.Bars[i] * h),
                });
                g.Children.Add(Ui.At(col, 0, i));
            }
            return g;
        }

        FrameworkElement Lines(Metric m, Theme t, int max)
        {
            var sp = new StackPanel();
            foreach (var (name, value) in m.Lines.Take(max))
            {
                var r = Ui.Cols(Ui.Star(), Ui.Auto);
                r.Children.Add(Ui.At(Txt(name, 12, t.Secondary, FontWeights.Normal), 0, 0));
                r.Children.Add(Ui.At(Txt(value, 12, t.Text, FontWeights.SemiBold), 0, 1));
                r.Margin = new Thickness(0, 1, 0, 1);
                sp.Children.Add(r);
            }
            return sp;
        }

        FrameworkElement BarOnly(double v, Brush b, Theme t, double h, double top = 6)
        {
            v = Ui.Clamp(v, 0, 1);
            var track = new Grid { Height = h, Margin = new Thickness(0, top, 0, 0) };
            track.Children.Add(new Border { Background = t.Track, CornerRadius = new CornerRadius(h / 2) });
            var fill = Ui.Cols(Ui.Star(Math.Max(0.0001, v)), Ui.Star(Math.Max(0.0001, 1 - v)));
            fill.Children.Add(Ui.At(new Border { Background = b, CornerRadius = new CornerRadius(h / 2) }, 0, 0));
            track.Children.Add(fill);
            return track;
        }

        UIElement Header(string text, string glyph, Brush accent, Theme t)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.Icon(glyph, 13, accent);
            icon.Margin = new Thickness(0, 1, 6, 0);
            sp.Children.Add(icon);
            sp.Children.Add(Txt(text, 13, t.Text, FontWeights.SemiBold));
            return sp;
        }

        public static FrameworkElement Ring(double size, double v, Brush b, Theme t, string center, double thick, double textScale = 1)
        {
            var grid = new Grid { Width = size, Height = size };
            grid.Children.Add(new Ellipse { Stroke = t.Track, StrokeThickness = thick, Margin = new Thickness(thick / 2) });
            v = Ui.Clamp(v, 0, 1);
            if (v >= 0.999)
                grid.Children.Add(new Ellipse { Stroke = b, StrokeThickness = thick, Margin = new Thickness(thick / 2) });
            else if (v > 0.001)
            {
                double r = (size - thick) / 2, c = size / 2, a = v * 2 * Math.PI;
                var fig = new PathFigure { StartPoint = new Point(c, c - r) };
                fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)),
                    new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true));
                grid.Children.Add(new Path
                {
                    Data = new PathGeometry(new[] { fig }), Stroke = b, StrokeThickness = thick,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
            double fs = size * 0.2 * textScale * (center.Length > 5 ? 0.8 : 1);
            var tb = Ui.Text(center, fs, t.Text, FontWeights.SemiBold, true);
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(tb);
            return grid;
        }

        static FrameworkElement Sparkline(List<double> data, Brush stroke, Theme t) =>
            new Spark { Data = data.ToArray(), Stroke = stroke, Grid = t.Separator };

        /// <summary>Gráfica histórica: área con degradado y línea, sobre tres guías discontinuas.</summary>
        sealed class Spark : FrameworkElement
        {
            public double[] Data;
            public Brush Stroke, Grid;

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                var guide = new Pen(Grid, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                for (int i = 0; i <= 2; i++) dc.DrawLine(guide, new Point(0, Math.Round(h * i / 2) + 0.5), new Point(w, Math.Round(h * i / 2) + 0.5));
                if (Data == null || Data.Length < 2) return;
                double step = w / 59, x0 = w - (Data.Length - 1) * step;
                var line = new StreamGeometry();
                var area = new StreamGeometry();
                using (var l = line.Open())
                using (var a = area.Open())
                {
                    for (int i = 0; i < Data.Length; i++)
                    {
                        var p = new Point(x0 + i * step, h - Data[i] * (h - 2) - 1);
                        if (i == 0)
                        {
                            l.BeginFigure(p, false, false);
                            a.BeginFigure(new Point(p.X, h), true, true);
                        }
                        else l.LineTo(p, true, true);
                        a.LineTo(p, false, false);
                    }
                    a.LineTo(new Point(w, h), false, false);
                }
                var c = Stroke is SolidColorBrush sc ? sc.Color : Colors.White;
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90), null, area);
                dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, line);
            }
        }
    }
}
