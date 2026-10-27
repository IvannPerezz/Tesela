using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Tesela
{
    /// <summary>
    /// Lecturas de hardware para el widget de sistema. Solo se usan fuentes reales; lo que no se puede
    /// leer queda en null y el widget lo muestra como no disponible:
    /// <list type="bullet">
    /// <item>CPU: nombre (registro), uso total y por núcleo y frecuencia efectiva (contadores de Windows,
    /// como el Administrador de tareas).</item>
    /// <item>GPU NVIDIA: NVML (nvml.dll, viene con el controlador): uso, temperatura, relojes, consumo,
    /// ventilador y VRAM. Otras GPU: uso y VRAM de los contadores de Windows.</item>
    /// <item>Memoria (velocidad por WMI), unidades, actividad de disco y red.</item>
    /// <item>Temperatura y consumo de la CPU, hotspot y VRAM de la GPU, placa base, chipset, ventiladores y
    /// voltajes: Windows no los da sin un controlador de sensores. Se leen de LibreHardwareMonitor (u
    /// OpenHardwareMonitor) si está abierto, que los publica por WMI.</item>
    /// </list>
    /// Todo se muestrea en un hilo aparte y solo los grupos que algún widget está mostrando.
    /// </summary>
    static class Hw
    {
        [Flags]
        public enum Group { None = 0, Cpu = 1, Cores = 2, Gpu = 4, Ram = 8, Disks = 16, DiskIo = 32, Net = 64, Sensors = 128 }

        public class Reading
        {
            public string Name;
            public double Value;
        }

        /// <summary>Una lectura completa (inmutable una vez publicada).</summary>
        public class Snapshot
        {
            public string CpuName;
            public double[] Cores;
            public double? CpuMhz, CpuMaxMhz, CpuTemp, CpuPower;

            public string GpuName;
            public double? GpuUsage, GpuTemp, GpuHotspot, GpuVramTemp, GpuCoreMhz, GpuMemMhz, GpuPower, GpuFan;
            public double? VramUsedGb, VramTotalGb;

            public double? RamMts;

            public List<(string Root, string Label, long Total, long Free)> Drives = new List<(string, string, long, long)>();
            public double? DiskActive, DiskRead, DiskWrite;

            public string NetName, NetIp;
            public double? NetDown, NetUp;
            public long NetSessionDown, NetSessionUp;

            public bool SensorsProvider;
            public List<Reading> BoardTemps = new List<Reading>(), Fans = new List<Reading>(), Volts = new List<Reading>();
        }

        public static volatile Snapshot Now = new Snapshot();

        /// <summary>Lecturas hechas hasta ahora (las dos primeras aún no tienen datos de velocidad).</summary>
        public static volatile int Count;

        static Group wanted;
        static int periodMs = 2000;
        static System.Threading.Timer timer;
        static readonly object sampling = new object();

        /// <summary>Pide que se muestreen estos grupos con este intervalo (lo llama cada widget al pintar).</summary>
        public static void Need(Group g, TimeSpan interval)
        {
            wanted |= g;
            int ms = (int)Math.Max(1000, interval.TotalMilliseconds);
            if (timer == null)
            {
                periodMs = ms;
                timer = new System.Threading.Timer(_ => SampleAll(), null, 0, periodMs);
            }
            else if (ms < periodMs)
            {
                periodMs = ms;
                timer.Change(0, periodMs);
            }
        }

        static void SampleAll()
        {
            if (!System.Threading.Monitor.TryEnter(sampling)) return;
            try
            {
                var prev = Now;
                var s = new Snapshot
                {
                    // Datos que no cambian: se arrastran de la lectura anterior
                    CpuName = prev.CpuName ?? CpuName(),
                    RamMts = prev.RamMts,
                    CpuMaxMhz = prev.CpuMaxMhz,
                    GpuName = prev.GpuName,
                    NetSessionDown = prev.NetSessionDown,
                    NetSessionUp = prev.NetSessionUp,
                };
                var g = wanted;
                if ((g & (Group.Cpu | Group.Cores)) != 0) SampleCpu(s, (g & Group.Cores) != 0);
                if ((g & Group.Gpu) != 0) SampleGpu(s);
                if ((g & Group.Ram) != 0 && s.RamMts == null) s.RamMts = RamSpeed();
                if ((g & Group.Disks) != 0) SampleDrives(s);
                if ((g & Group.DiskIo) != 0) SampleDiskIo(s);
                if ((g & Group.Net) != 0) SampleNet(s);
                if ((g & (Group.Sensors | Group.Cpu | Group.Gpu)) != 0) SampleSensors(s);
                Now = s;
                Count++;
            }
            catch (Exception ex) { Program.Log("hw: " + ex.Message); }
            finally { System.Threading.Monitor.Exit(sampling); }
        }

        // ---- CPU ---------------------------------------------------------------------------------

        static string CpuName()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return (k?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            catch { return null; }
        }

        static double BaseMhz()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return Convert.ToDouble(k?.GetValue("~MHz") ?? 0);
            }
            catch { return 0; }
        }

        static PerformanceCounterCategory procCat;
        static InstanceDataCollectionCollection lastProc;
        static double baseMhz = -1;

        static void SampleCpu(Snapshot s, bool cores)
        {
            try
            {
                if (procCat == null) procCat = new PerformanceCounterCategory("Processor Information");
                if (baseMhz < 0) baseMhz = BaseMhz();
                var now = procCat.ReadCategory();
                if (lastProc != null)
                {
                    // Frecuencia efectiva = frecuencia base × "% de rendimiento del procesador" (como el Administrador de tareas)
                    var perf = Calc(now, lastProc, "% Processor Performance", "_Total");
                    if (perf.HasValue && baseMhz > 0)
                    {
                        s.CpuMhz = baseMhz * perf.Value / 100;
                        s.CpuMaxMhz = Math.Max(s.CpuMaxMhz ?? 0, s.CpuMhz.Value);
                    }
                    if (cores)
                    {
                        var list = new List<(int, int, double)>();
                        var col = now["% Processor Utility"] ?? now["% Processor Time"];
                        if (col != null)
                            foreach (InstanceData d in col.Values)
                            {
                                var parts = d.InstanceName.Split(',');
                                if (parts.Length != 2 || parts[1] == "_Total" || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) continue;
                                var v = Calc(now, lastProc, col == now["% Processor Utility"] ? "% Processor Utility" : "% Processor Time", d.InstanceName);
                                if (v.HasValue) list.Add((a, b, Ui.Clamp(v.Value / 100, 0, 1)));
                            }
                        s.Cores = list.OrderBy(x => x.Item1).ThenBy(x => x.Item2).Select(x => x.Item3).ToArray();
                    }
                }
                lastProc = now;
            }
            catch (Exception ex) { Program.Log("hw cpu: " + ex.Message); }
        }

        static double? Calc(InstanceDataCollectionCollection now, InstanceDataCollectionCollection prev, string counter, string instance)
        {
            try
            {
                var a = prev[counter]?[instance];
                var b = now[counter]?[instance];
                if (a == null || b == null) return null;
                return CounterSample.Calculate(a.Sample, b.Sample);
            }
            catch { return null; }
        }

        // ---- GPU (NVIDIA: NVML) --------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong Total, Free, Used; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int NvInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int NvHandle(uint i, out IntPtr h);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] static extern int NvName(IntPtr h, StringBuilder name, uint len);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int NvTemp(IntPtr h, int sensor, out uint t);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int NvUtilization(IntPtr h, out NvUtil u);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] static extern int NvClock(IntPtr h, int type, out uint mhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int NvPower(IntPtr h, out uint mw);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")] static extern int NvFan(IntPtr h, out uint pct);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int NvMemory(IntPtr h, out NvMem m);

        static int nvState; // 0 = sin probar, 1 = listo, -1 = no hay NVML
        static IntPtr nvDev;

        static void SampleGpu(Snapshot s)
        {
            if (nvState == 0)
            {
                try
                {
                    nvState = NvInit() == 0 && NvHandle(0, out nvDev) == 0 ? 1 : -1;
                    if (nvState == 1)
                    {
                        var sb = new StringBuilder(96);
                        if (NvName(nvDev, sb, 96) == 0) s.GpuName = sb.ToString();
                    }
                }
                catch { nvState = -1; } // sin NVIDIA o sin el controlador
            }
            if (nvState == 1)
            {
                try
                {
                    if (NvUtilization(nvDev, out var u) == 0) s.GpuUsage = u.Gpu / 100.0;
                    if (NvTemp(nvDev, 0, out var t) == 0) s.GpuTemp = t;
                    if (NvClock(nvDev, 0, out var c) == 0) s.GpuCoreMhz = c;
                    if (NvClock(nvDev, 2, out var m) == 0) s.GpuMemMhz = m;
                    if (NvPower(nvDev, out var p) == 0) s.GpuPower = p / 1000.0;
                    if (NvFan(nvDev, out var f) == 0) s.GpuFan = f / 100.0;
                    if (NvMemory(nvDev, out var mem) == 0 && mem.Total > 0)
                    {
                        s.VramUsedGb = mem.Used / 1073741824.0;
                        s.VramTotalGb = mem.Total / 1073741824.0;
                    }
                }
                catch (Exception ex) { Program.Log("hw nvml: " + ex.Message); nvState = -1; }
            }
            if (nvState != 1)
            {
                // Otras GPU: los contadores de Windows (como el Administrador de tareas)
                s.GpuUsage = SysInfo.Gpu;
                if (SysInfo.GpuMemGb > 0) s.VramUsedGb = SysInfo.GpuMemGb;
                if (s.GpuName == null) s.GpuName = VideoName();
            }
        }

        static string VideoName()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject o in q.Get())
                    {
                        var n = o["Name"] as string;
                        if (!string.IsNullOrEmpty(n) && n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) < 0) return n;
                    }
            }
            catch { }
            return "";
        }

        // ---- Memoria ---------------------------------------------------------------------------------

        static double? RamSpeed()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory"))
                {
                    double best = 0;
                    foreach (ManagementObject o in q.Get())
                    {
                        var v = Convert.ToDouble(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
                        if (v > best) best = v;
                    }
                    return best > 0 ? best : (double?)null;
                }
            }
            catch { return null; }
        }

        // ---- Almacenamiento --------------------------------------------------------------------------

        static void SampleDrives(Snapshot s)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    s.Drives.Add((d.RootDirectory.FullName, string.IsNullOrEmpty(d.VolumeLabel) ? "Disco local" : d.VolumeLabel, d.TotalSize, d.TotalFreeSpace));
                }
                catch { }
            }
        }

        static PerformanceCounterCategory diskCat;
        static InstanceDataCollectionCollection lastDisk;

        static void SampleDiskIo(Snapshot s)
        {
            try
            {
                if (diskCat == null) diskCat = new PerformanceCounterCategory("PhysicalDisk");
                var now = diskCat.ReadCategory();
                if (lastDisk != null)
                {
                    var idle = Calc(now, lastDisk, "% Idle Time", "_Total");
                    if (idle.HasValue) s.DiskActive = Ui.Clamp(1 - idle.Value / 100, 0, 1);
                    s.DiskRead = Calc(now, lastDisk, "Disk Read Bytes/sec", "_Total");
                    s.DiskWrite = Calc(now, lastDisk, "Disk Write Bytes/sec", "_Total");
                }
                lastDisk = now;
            }
            catch (Exception ex) { Program.Log("hw disk: " + ex.Message); }
        }

        // ---- Red -------------------------------------------------------------------------------------

        static long lastRx = -1, lastTx;
        static DateTime lastNet;
        static string lastNic;

        static void SampleNet(Snapshot s)
        {
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();
                // El adaptador activo: el que tiene puerta de enlace (si no, el que más tráfico ha movido)
                var nic = nics.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    ?? nics.OrderByDescending(n => n.GetIPv4Statistics().BytesReceived).FirstOrDefault();
                if (nic == null) return;
                s.NetName = nic.Description;
                s.NetIp = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                var st = nic.GetIPv4Statistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                var t = DateTime.UtcNow;
                if (lastRx >= 0 && lastNic == nic.Id)
                {
                    double secs = (t - lastNet).TotalSeconds;
                    if (secs > 0.2)
                    {
                        s.NetDown = Math.Max(0, (rx - lastRx) / secs);
                        s.NetUp = Math.Max(0, (tx - lastTx) / secs);
                        s.NetSessionDown += Math.Max(0, rx - lastRx);
                        s.NetSessionUp += Math.Max(0, tx - lastTx);
                    }
                }
                lastRx = rx;
                lastTx = tx;
                lastNet = t;
                lastNic = nic.Id;
            }
            catch (Exception ex) { Program.Log("hw net: " + ex.Message); }
        }

        // ---- Sensores (LibreHardwareMonitor / OpenHardwareMonitor) -----------------------------------

        static DateTime nextProbe;
        /// <summary>De dónde salen los sensores ahora: "Tesela", "LibreHardwareMonitor" o null.</summary>
        public static string SensorSource { get; private set; }
        static string sensorNs;     // espacio WMI (versiones antiguas de LibreHardwareMonitor / OpenHardwareMonitor)
        static bool sensorHttp;     // servidor web de LibreHardwareMonitor (versiones actuales)
        const string SensorUrl = "http://localhost:8085/data.json";

        /// <summary>Lecturas de LibreHardwareMonitor: (nombre, tipo, valor, identificador del hardware).</summary>
        static List<(string Name, string Type, double Value, string Parent)> ReadSensors()
        {
            // Primero, los sensores integrados de Tesela (sin tener LibreHardwareMonitor abierto)
            var own = Sensors.Read();
            if (own != null && own.Count > 0)
            {
                SensorSource = "Tesela";
                return own;
            }
            SensorSource = sensorNs != null || sensorHttp ? "LibreHardwareMonitor" : null;
            if (sensorNs == null && !sensorHttp)
            {
                // Buscar un proveedor abierto, como mucho cada 30 s
                if (DateTime.UtcNow < nextProbe) return null;
                nextProbe = DateTime.UtcNow.AddSeconds(30);
                foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
                {
                    try
                    {
                        using (var q = new ManagementObjectSearcher(ns, "SELECT Name FROM Sensor"))
                            if (q.Get().Count > 0) { sensorNs = ns; break; }
                    }
                    catch { }
                }
                if (sensorNs == null)
                {
                    var probe = ReadHttp();
                    if (probe == null || probe.Count == 0) return null;
                    sensorHttp = true;
                    SensorSource = "LibreHardwareMonitor";
                    return probe;
                }
            }
            if (sensorHttp)
            {
                var r = ReadHttp();
                if (r == null) sensorHttp = false; // se ha cerrado
                return r;
            }
            var all = new List<(string, string, double, string)>();
            using (var q = new ManagementObjectSearcher(sensorNs, "SELECT Name, SensorType, Value, Parent FROM Sensor"))
                foreach (ManagementObject o in q.Get())
                    all.Add(((string)o["Name"] ?? "", (string)o["SensorType"] ?? "", Convert.ToDouble(o["Value"] ?? 0), ((string)o["Parent"] ?? "").ToLowerInvariant()));
            return all;
        }

        /// <summary>
        /// LibreHardwareMonitor 0.9.x ya no publica por WMI: sus lecturas salen de su "Servidor web remoto"
        /// (Opciones → Servidor web remoto → Ejecutar), un árbol JSON en http://localhost:8085/data.json.
        /// </summary>
        static List<(string Name, string Type, double Value, string Parent)> ReadHttp()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(SensorUrl);
                req.Timeout = req.ReadWriteTimeout = 800;
                req.Proxy = null;
                string json;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();
                var root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
                var list = new List<(string, string, double, string)>();
                Walk(root, list);
                return list;
            }
            catch { return null; }
        }

        static void Walk(Dictionary<string, object> node, List<(string, string, double, string)> list)
        {
            if (node == null) return;
            // Un sensor tiene SensorId, p. ej. "/amdcpu/0/temperature/2" o "/lpc/nct6798d/0/fan/1"
            if (node.TryGetValue("SensorId", out var idObj) && idObj is string id && id.Length > 0)
            {
                var parts = id.Trim('/').Split('/');
                string type = node.TryGetValue("Type", out var t) && t is string ts && ts.Length > 0 ? ts : parts.Length >= 2 ? parts[parts.Length - 2] : "";
                type = type.Length > 0 ? char.ToUpperInvariant(type[0]) + type.Substring(1) : type;
                var parent = "/" + string.Join("/", parts.Take(Math.Max(1, parts.Length - 2)));
                var value = node.TryGetValue("Value", out var v) ? ParseNumber(v as string) : null;
                if (value.HasValue) list.Add(((node.TryGetValue("Text", out var tx) ? tx as string : "") ?? "", type, value.Value, parent.ToLowerInvariant()));
            }
            if (node.TryGetValue("Children", out var ch) && ch is object[] kids)
                foreach (var k in kids) Walk(k as Dictionary<string, object>, list);
        }

        /// <summary>"45,3 °C", "1105 RPM", "1,105 V"… → número. LibreHardwareMonitor usa el formato
        /// regional para la coma decimal y no pone separador de miles.</summary>
        static double? ParseNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
            if (!m.Success) return null;
            return double.TryParse(m.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        }

        static void SampleSensors(Snapshot s)
        {
            try
            {
                var all = ReadSensors();
                if (all == null) return;
                {
                    s.SensorsProvider = all.Count > 0;
                    bool Cpu(string p) => p.Contains("cpu") && !p.Contains("gpu");
                    bool Gpu(string p) => p.Contains("gpu");
                    bool Board(string p) => p.Contains("lpc") || p.Contains("mainboard") || p.Contains("motherboard");

                    // Temperatura de la CPU: la que mide la placa base (sensor "CPU" del chip de la placa, p. ej.
                    // Nuvoton NCT6798D), que es la que se usa para las curvas de ventiladores y oscila menos que
                    // Tctl. Si la placa no la da, la del controlador embebido y, por último, la del propio procesador.
                    var boardCpu = all.Where(x => x.Type == "Temperature" && Board(x.Parent) && x.Value > 0
                        && x.Name.Trim().Equals("CPU", StringComparison.OrdinalIgnoreCase)).ToList();
                    var bc = boardCpu.FirstOrDefault(x => !x.Parent.Contains("/ec"));
                    if (bc.Name == null) bc = boardCpu.FirstOrDefault();
                    var cpuTemps = all.Where(x => x.Type == "Temperature" && Cpu(x.Parent)).ToList();
                    var ct = cpuTemps.FirstOrDefault(x => x.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0
                        || x.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (bc.Name != null) s.CpuTemp = bc.Value;
                    else if (ct.Name != null) s.CpuTemp = ct.Value;
                    else if (cpuTemps.Count > 0) s.CpuTemp = cpuTemps.Max(x => x.Value);
                    var cp = all.FirstOrDefault(x => x.Type == "Power" && Cpu(x.Parent) && x.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (cp.Name != null) s.CpuPower = cp.Value;

                    var hot = all.FirstOrDefault(x => x.Type == "Temperature" && Gpu(x.Parent) && x.Name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hot.Name != null) s.GpuHotspot = hot.Value;
                    var vt = all.FirstOrDefault(x => x.Type == "Temperature" && Gpu(x.Parent) && x.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (vt.Name != null) s.GpuVramTemp = vt.Value;
                    if (s.GpuTemp == null)
                    {
                        var gt = all.FirstOrDefault(x => x.Type == "Temperature" && Gpu(x.Parent) && x.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (gt.Name != null) s.GpuTemp = gt.Value;
                    }

                    foreach (var x in all.Where(x => x.Type == "Temperature" && Board(x.Parent) && x.Value > 0 && x.Value < 150))
                        s.BoardTemps.Add(new Reading { Name = x.Name, Value = x.Value });
                    foreach (var x in all.Where(x => x.Type == "Fan" && x.Value > 0))
                        s.Fans.Add(new Reading { Name = x.Name, Value = x.Value });
                    foreach (var x in all.Where(x => x.Type == "Voltage" && Board(x.Parent)))
                        s.Volts.Add(new Reading { Name = x.Name, Value = x.Value });
                }
            }
            catch (Exception ex)
            {
                // El proveedor se ha cerrado: se vuelve a buscar más tarde
                sensorNs = null;
                Program.Log("hw sensors: " + ex.Message);
            }
        }
    }
}
