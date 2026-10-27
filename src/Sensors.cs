using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Tesela
{
    /// <summary>
    /// Sensores integrados: temperaturas de CPU y placa, ventiladores y voltajes leídos con la librería de
    /// LibreHardwareMonitor (MPL 2.0) que va con Tesela en bin\sensors, sin tener que abrir el programa.
    ///
    /// Leer el chip de sensores de la placa necesita permisos de administrador, así que no lo hace Tesela
    /// (que sigue sin ellos) sino un proceso auxiliar: "Tesela.exe --sensor-host". Se arranca con una tarea
    /// programada de Windows con privilegios elevados, que se crea una sola vez (con un aviso de UAC); a
    /// partir de ahí Tesela la lanza sin preguntar. El auxiliar publica las lecturas por una tubería con
    /// nombre y se cierra solo cuando Tesela se cierra.
    /// </summary>
    static class Sensors
    {
        public const string PipeName = "Tesela.Sensors";
        const string TaskName = @"Tesela\Sensores";
        const string MainMutex = "Tesela.SingleInstance";
        const string HostMutex = "Tesela.SensorHost";

        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sensors");
        public static bool Bundled => File.Exists(Path.Combine(Folder, "LibreHardwareMonitorLib.dll"));

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>La librería y sus dependencias se cargan de bin\sensors, sin mirar la versión exacta.</summary>
        public static void HookAssemblies()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = Path.Combine(Folder, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
        }

        // ---- Lado de Tesela (sin permisos) -------------------------------------------------------------

        static DateTime nextStart;

        /// <summary>Lecturas del auxiliar, o null si no está en marcha (entonces se intenta arrancar).</summary>
        public static List<(string Name, string Type, double Value, string Parent)> Read()
        {
            if (!Bundled || !Enabled) return null;
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In))
                {
                    pipe.Connect(250);
                    string text;
                    using (var sr = new StreamReader(pipe, Encoding.UTF8)) text = sr.ReadToEnd();
                    var list = new List<(string, string, double, string)>();
                    foreach (var line in text.Split('\n'))
                    {
                        var f = line.Split('\t');
                        if (f.Length == 4 && double.TryParse(f[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                            list.Add((f[0], f[1], v, f[3]));
                    }
                    return list;
                }
            }
            catch
            {
                // No responde: se arranca (como mucho cada 20 s)
                if (DateTime.UtcNow >= nextStart)
                {
                    nextStart = DateTime.UtcNow.AddSeconds(20);
                    Start();
                }
                return null;
            }
        }

        /// <summary>true si ya se dio permiso (existe la tarea programada) o Tesela va con permisos.</summary>
        public static bool Enabled => IsElevated || TaskExists();

        static bool? taskExists;
        static DateTime taskCheckedAt;

        /// <summary>¿Existe la tarea programada? (se consulta como mucho cada minuto)</summary>
        static bool TaskExists(bool fresh = false)
        {
            if (fresh || taskExists == null || (DateTime.UtcNow - taskCheckedAt).TotalSeconds > 60)
            {
                taskExists = RunHidden("schtasks.exe", $"/query /tn \"{TaskName}\"") == 0;
                taskCheckedAt = DateTime.UtcNow;
            }
            return taskExists.Value;
        }

        static void Start()
        {
            try
            {
                if (IsElevated)
                    Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--sensor-host")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                    });
                else if (TaskExists())
                    RunHidden("schtasks.exe", $"/run /tn \"{TaskName}\"");
            }
            catch (Exception ex) { Program.Log("sensores: " + ex.Message); }
        }

        /// <summary>
        /// Da permiso a los sensores integrados: pide confirmación de UAC una vez y crea la tarea programada.
        /// Devuelve false si se canceló.
        /// </summary>
        public static bool Enable()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--install-sensors")
                {
                    UseShellExecute = true, Verb = "runas",
                });
                p?.WaitForExit(20000);
                nextStart = DateTime.MinValue;
                return TaskExists(fresh: true);
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC cancelado
        }

        /// <summary>Quita la tarea programada (con UAC) y cierra el auxiliar.</summary>
        public static void Disable()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--uninstall-sensors")
                {
                    UseShellExecute = true, Verb = "runas",
                });
                p?.WaitForExit(20000);
                TaskExists(fresh: true);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }

        // ---- Instalación (proceso elevado) -------------------------------------------------------------

        public static void Install()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var user = WindowsIdentity.GetCurrent().Name;
            string X(string s) => System.Security.SecurityElement.Escape(s);
            // Tarea sin desencadenadores (solo se lanza a petición de Tesela), con los privilegios más altos
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Sensores de hardware para los widgets de Tesela</Description></RegistrationInfo>
  <Principals>
    <Principal id=""Author""><UserId>{X(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <Hidden>true</Hidden>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{X(exe)}</Command><Arguments>--sensor-host</Arguments><WorkingDirectory>{X(Path.GetDirectoryName(exe))}</WorkingDirectory></Exec>
  </Actions>
</Task>";
            var file = Path.Combine(Path.GetTempPath(), "tesela-sensores.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            int code = RunHidden("schtasks.exe", $"/create /f /tn \"{TaskName}\" /xml \"{file}\"");
            try { File.Delete(file); } catch { }
            if (code != 0) Program.Log("sensores: no se pudo crear la tarea (" + code + ")");
            else RunHidden("schtasks.exe", $"/run /tn \"{TaskName}\"");
        }

        public static void Uninstall()
        {
            RunHidden("schtasks.exe", $"/end /tn \"{TaskName}\"");
            RunHidden("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
        }

        static int RunHidden(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                }))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // ---- Auxiliar (proceso elevado) ----------------------------------------------------------------

        public static void Host()
        {
            using (var single = new Mutex(true, HostMutex, out bool created))
            {
                if (!created) return;
                // Se cierra solo cuando Tesela ya no está abierto
                var watch = new Thread(() =>
                {
                    int missing = 0;
                    while (true)
                    {
                        Thread.Sleep(3000);
                        bool open = Mutex.TryOpenExisting(MainMutex, out var m);
                        m?.Dispose();
                        missing = open ? 0 : missing + 1;
                        if (missing >= 3) Environment.Exit(0);
                    }
                }) { IsBackground = true };
                watch.Start();
                try { Serve(); }
                catch (Exception ex) { Program.Log("sensores (auxiliar): " + ex); }
            }
        }

        static void Serve()
        {
            var reader = new LhmReader();
            string last = "";
            var lastAt = DateTime.MinValue;
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (true)
            {
                using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                           PipeOptions.None, 0, 0, security))
                {
                    pipe.WaitForConnection();
                    // Tesela pregunta cada segundo como mucho; se relee el hardware si han pasado 0,8 s
                    if ((DateTime.UtcNow - lastAt).TotalMilliseconds > 800)
                    {
                        last = reader.Text();
                        lastAt = DateTime.UtcNow;
                    }
                    var bytes = Encoding.UTF8.GetBytes(last);
                    try
                    {
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                    catch (IOException) { }
                }
            }
        }
    }

    /// <summary>
    /// Lectura con LibreHardwareMonitorLib (en una clase aparte: la librería solo se carga en el auxiliar).
    /// Cada sensor sale como "nombre \t tipo \t valor \t hardware", con los mismos identificadores que
    /// el servidor web de LibreHardwareMonitor (p. ej. "/lpc/nct6798d/0").
    /// </summary>
    class LhmReader
    {
        readonly LibreHardwareMonitor.Hardware.Computer computer;

        public LhmReader()
        {
            computer = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true,
            };
            computer.Open();
        }

        public string Text()
        {
            var sb = new StringBuilder();
            foreach (var hw in computer.Hardware) Add(hw, sb);
            return sb.ToString();
        }

        static void Add(LibreHardwareMonitor.Hardware.IHardware hw, StringBuilder sb)
        {
            try { hw.Update(); } catch { }
            foreach (var s in hw.Sensors)
            {
                if (!(s.Value is float v) || float.IsNaN(v)) continue;
                sb.Append((s.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')).Append('\t')
                  .Append(s.SensorType.ToString()).Append('\t')
                  .Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                  .Append(hw.Identifier.ToString().ToLowerInvariant()).Append('\n');
            }
            foreach (var sub in hw.SubHardware) Add(sub, sb);
        }
    }
}
