using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Pcp.Agent.Services;

/// <summary>
/// Servidor VNC local controlado por el agente.
/// Se soporta TightVNC 2.8.x en modo aplicación (sin servicio): la configuración se escribe en
/// HKCU\Software\TightVNC\Server, el RFB escucha en un puerto local y el agente lo publica al
/// panel por su túnel WebSocket, de forma que la mini-PC no abre puertos entrantes.
///
/// Notas verificadas contra el código de TightVNC 2.8 (server-config-lib/Configurator.cpp y
/// rfb-sconn/RfbInitializer.cpp), para no repetir experimentos:
///  - <c>AllowLoopback</c> vale 0 por defecto; con ese valor TightVNC aborta el handshake de
///    cualquier conexión desde 127.0.0.1 con "Sorry, loopback connections are not enabled".
///    Ese texto lo emite TightVNC, no Kestrel ni el túnel WebSocket: el túnel funciona igual
///    sobre ws:// sin TLS. Por eso el agente escribe siempre AllowLoopback=1.
///  - <c>LoopbackOnly</c> = 1 restringe el RFB a 127.0.0.1 (el túnel es el único camino).
///  - La contraseña de VNC se guarda ofuscada con DES (clave fija del protocolo RFB) y se
///    relee únicamente al arrancar el servidor, así que cada sesión reinicia el proceso.
///  - <c>tvnserver.exe</c> 2.8 solo acepta <c>-run</c> (y las opciones de ayuda). Cualquier otro
///    argumento, incluido <c>-port</c>, hace que muestre la ayuda y termine sin arrancar;
///    el puerto se indica siempre en el valor <c>RfbPort</c> del registro.
/// </summary>
public sealed class VncServerManager
{
    private const string RegistryPath = @"Software\TightVNC\Server";
    private const string ServiceOnlyPath = @"Software\TightVNC\Server\ServiceOnly";
    private const string ExecutableName = "tvnserver.exe";

    /// <summary>Ruta habitual de la instalación de TightVNC 2.8.90 en las mini-PC.</summary>
    private const string DefaultExecutablePath = @"C:\Program Files\TightVNC\tvnserver.exe";

    private const int FirstVncPort = 5900;
    private const int LastVncPort = 5999;

    /// <summary>Clave fija del esquema de ofuscación de contraseña de VNC (DES).</summary>
    private static readonly byte[] VncDesKey = { 23, 82, 107, 6, 35, 78, 88, 7 };

    /// <summary>
    /// Clave DES usada para almacenar la contraseña: la fija del protocolo con los bits de cada
    /// byte invertidos (el d3des de VNC lee la clave con el orden de bits invertido; ver
    /// util/DesCrypt.cpp, "bytebit reversed for VNC compatibility").
    /// </summary>
    private static readonly byte[] VncStorageKey = VncDesKey.Select(ReverseBits).ToArray();

    /// <summary>Valores de registro que el agente toca en HKCU y restaura al cerrar la sesión.</summary>
    private static readonly string[] ManagedValues =
    {
        "RfbPort",
        "AcceptRfbConnections",
        "AcceptHttpConnections",
        "UseVncAuthentication",
        "Password",
        "PasswordViewOnly",
        "BlockRemoteInput",
        "AllowLoopback",
        "LoopbackOnly",
        "UseD3D",
        "GrabTransparentWindows",
        "AlwaysShared",
        "NeverShared",
        "EnableFileTransfers",
        "RunControlInterface",
        "IdleTimeout",
        "ExtraPorts"
    };

    private readonly IConfiguration _config;
    private readonly ILogger<VncServerManager> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RegistrySnapshot> _machineSnapshots = new();

    private Process? _process;
    private RegistrySnapshot? _userSnapshot;
    private DateTime _availabilityCheckedAt = DateTime.MinValue;
    private bool _availability;
    private string _availabilityDetail = "Sin comprobar";
    private string? _executablePath;

    public VncServerManager(IConfiguration config, ILogger<VncServerManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool Enabled => _config.GetValue("Agent:Vnc:Enabled", true);

    /// <summary>Indica si hay (o se puede aprovisionar) un TightVNC para control remoto.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureAvailabilityProbed();
            return _availability;
        }
    }

    /// <summary>Motivo legible por el operador cuando el control remoto no está disponible.</summary>
    public string AvailabilityDetail
    {
        get
        {
            EnsureAvailabilityProbed();
            return _availabilityDetail;
        }
    }

    private string InstallDir
    {
        get
        {
            var configured = _config["Agent:Vnc:InstallDir"];
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Pcp", "vnc")
                : configured;
        }
    }

    private string? PackagePath => _config["Agent:Vnc:PackagePath"];

    private int StartupTimeoutSeconds => Math.Max(3, _config.GetValue("Agent:Vnc:StartupTimeoutSeconds", 12));

    /// <summary>
    /// Inicia el servidor VNC en un puerto local con la contraseña indicada.
    /// Devuelve el puerto realmente usado. Lanza una excepción con un mensaje accionable si falla.
    /// </summary>
    public async Task<int> StartAsync(string password, int preferredPort, bool viewOnly, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false })
            {
                // La contraseña RFB es efímera y TightVNC la lee del registro solo al arrancar:
                // reutilizar el proceso dejaría la contraseña de la sesión anterior.
                _logger.LogInformation("Reiniciando TightVNC para aplicar la contraseña de la nueva sesión.");
                await StopInternalAsync();
            }

            var exe = ResolveExecutable()
                ?? throw new InvalidOperationException(
                    $"No se encontró TightVNC 2.8 en la mini-PC (se esperaba {DefaultExecutablePath}). " +
                    "Instálalo o define Agent:Vnc:ExecutablePath con la ruta de tvnserver.exe.");

            EnsureApplicationModeAllowed();

            var survivors = KillLeftoverInstances();

            var port = SelectPort(preferredPort);

            try
            {
                ApplyConfiguration(port, password, viewOnly);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = BuildArguments(),
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                _logger.LogInformation("Iniciando TightVNC en 127.0.0.1:{Port} (modo aplicación)", port);
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("No se pudo iniciar el proceso de TightVNC.");

                var output = CaptureOutputAsync(_process);

                if (!await WaitForListeningAsync(port, _process, ct))
                {
                    var detail = _process.HasExited
                        ? $"TightVNC terminó con código {_process.ExitCode} sin abrir el puerto {port}. " +
                          await SafeReadAsync(output)
                        : $"TightVNC no abrió el puerto {port} en {StartupTimeoutSeconds} s. " +
                          "Comprueba que el servicio TightVNC no esté activo y que el agente corra " +
                          "en la sesión interactiva del usuario." +
                          (survivors.Count > 0
                              ? " Hay instancias de TightVNC que el agente no puede cerrar " +
                                $"(PID {string.Join(", ", survivors)}); ciérralas o ejecuta el agente como administrador."
                              : string.Empty);

                    throw new InvalidOperationException(detail.Trim());
                }

                await VerifySessionPasswordAsync(port, password, ct);
                WarnIfExposed(port);

                _logger.LogInformation("TightVNC escuchando en 127.0.0.1:{Port}", port);
                return port;
            }
            catch
            {
                // Cualquier fallo tras escribir el registro deja el VNC detenido y la
                // configuración del usuario restaurada.
                await StopInternalAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Detiene el VNC lanzado por el agente y restaura la configuración previa del registro.
    /// </summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo detener TightVNC: {Message}", ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }

        try
        {
            _userSnapshot?.Restore();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo restaurar la configuración de TightVNC: {Message}", ex.Message);
        }
        finally
        {
            _userSnapshot = null;
        }

        foreach (var snapshot in _machineSnapshots)
        {
            try
            {
                snapshot.Restore();
            }
            catch (Exception ex)
            {
                _logger.LogDebug("No se pudo restaurar AllowLoopback en HKLM: {Message}", ex.Message);
            }
        }

        _machineSnapshots.Clear();
    }

    private void EnsureAvailabilityProbed()
    {
        if (DateTime.UtcNow - _availabilityCheckedAt < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _availabilityCheckedAt = DateTime.UtcNow;

        if (!Enabled)
        {
            _availability = false;
            _availabilityDetail = "El control remoto está deshabilitado en Agent:Vnc:Enabled.";
            return;
        }

        try
        {
            _executablePath = FindExecutable(_executablePath);

            if (_executablePath != null)
            {
                _availability = true;
                _availabilityDetail = $"TightVNC detectado en {_executablePath}.";
                return;
            }

            var package = PackagePath;
            if (!string.IsNullOrWhiteSpace(package) && File.Exists(package))
            {
                _availability = true;
                _availabilityDetail = $"TightVNC se aprovisionará desde {package} al abrir la primera sesión.";
                return;
            }

            _availability = false;
            _availabilityDetail =
                $"TightVNC 2.8 no está instalado (se esperaba {DefaultExecutablePath}). " +
                "Instálalo en la mini-PC o configura Agent:Vnc:ExecutablePath.";
        }
        catch (Exception ex)
        {
            _availability = false;
            _availabilityDetail = $"No se pudo comprobar TightVNC: {ex.Message}";
        }
    }

    /// <summary>Busca el ejecutable y, si no existe, intenta aprovisionarlo desde Agent:Vnc:PackagePath.</summary>
    private string? ResolveExecutable()
    {
        var found = FindExecutable(_executablePath);
        if (found != null)
        {
            _executablePath = found;
            return found;
        }

        var package = PackagePath;
        if (string.IsNullOrWhiteSpace(package) || !File.Exists(package))
        {
            return null;
        }

        try
        {
            Provision(package);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al aprovisionar TightVNC desde {Package}", package);
            _availability = false;
            _availabilityDetail = $"No se pudo aprovisionar TightVNC: {ex.Message}";
            _availabilityCheckedAt = DateTime.UtcNow;
            return null;
        }

        found = FindExecutable(null);
        if (found != null)
        {
            _executablePath = found;
            _availability = true;
            _availabilityDetail = $"TightVNC aprovisionado en {found}.";
            _availabilityCheckedAt = DateTime.UtcNow;
        }

        return found;
    }

    private void Provision(string package)
    {
        var extension = Path.GetExtension(package).ToLowerInvariant();

        if (extension == ".zip")
        {
            Directory.CreateDirectory(InstallDir);
            _logger.LogInformation("Extrayendo TightVNC portable en {Dir}", InstallDir);
            ZipFile.ExtractToDirectory(package, InstallDir, overwriteFiles: true);
            return;
        }

        // Instaladores silenciosos: requieren privilegios de administrador en la mini-PC.
        var startInfo = extension == ".msi"
            ? new ProcessStartInfo("msiexec.exe", $"/i \"{package}\" /qn /norestart")
            : new ProcessStartInfo(package, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-");

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var installer = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar el instalador de TightVNC.");

        if (!installer.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            throw new TimeoutException("El instalador de TightVNC no terminó en 3 minutos.");
        }

        if (installer.ExitCode != 0 && installer.ExitCode != 3010)
        {
            throw new InvalidOperationException(
                $"El instalador de TightVNC terminó con código {installer.ExitCode}. " +
                "Ejecuta el agente como administrador o usa un zip portable.");
        }
    }

    private string? FindExecutable(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint))
        {
            return hint;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Orden de búsqueda del ejecutable: configurado, ruta estándar de 2.8.90 y portable.</summary>
    private IEnumerable<string> EnumerateCandidates()
    {
        var configured = _config["Agent:Vnc:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return DefaultExecutablePath;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "TightVNC", ExecutableName);
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "TightVNC", ExecutableName);
        }

        yield return Path.Combine(InstallDir, ExecutableName);
    }

    private string BuildArguments()
    {
        var configured = _config.GetSection("Agent:Vnc:Args").Get<string[]>();
        if (configured is { Length: > 0 })
        {
            return string.Join(' ', configured.Select(QuoteArgument));
        }

        // Modo aplicación con la configuración de HKCU. El puerto va en RfbPort: tvnserver 2.8
        // rechaza "-port" y terminaría mostrando la ayuda.
        return "-run";
    }

    private static string QuoteArgument(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// TightVNC 2.8 se niega a arrancar en modo aplicación (con un diálogo que bloquea el proceso)
    /// cuando algún administrador dejó la marca "ServiceOnly" en HKLM. El control remoto necesita
    /// el modo aplicación, así que la marca se retira antes de arrancar.
    /// </summary>
    private void EnsureApplicationModeAllowed()
    {
        foreach (var view in MachineViews)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = SafeOpen(root, ServiceOnlyPath, writable: false);
            if (key is null)
            {
                continue;
            }

            if (!_config.GetValue("Agent:Vnc:ForceApplicationMode", true))
            {
                throw new InvalidOperationException(
                    "TightVNC está forzado a modo servicio (HKLM\\" + ServiceOnlyPath + "). " +
                    "Elimina esa clave para usar el control remoto en modo aplicación.");
            }

            try
            {
                root.DeleteSubKeyTree(ServiceOnlyPath, throwOnMissingSubKey: false);
                _logger.LogWarning(
                    "Se retiró HKLM\\{Path} ({View}): TightVNC quedaba bloqueado en modo servicio.",
                    ServiceOnlyPath, view);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "TightVNC está forzado a modo servicio (HKLM\\" + ServiceOnlyPath + ") y no se pudo " +
                    $"retirar la marca: {ex.Message}. Ejecuta el agente como administrador o elimina la clave.");
            }
        }
    }

    /// <summary>
    /// Elige el puerto local del RFB: el solicitado, uno libre del rango VNC (5900-5999)
    /// o, como último recurso, un puerto efímero libre.
    /// </summary>
    private static int SelectPort(int preferredPort)
    {
        if (preferredPort > 0)
        {
            if (!IsPortFree(preferredPort))
            {
                throw new InvalidOperationException(
                    $"El puerto VNC {preferredPort} ya está ocupado en la mini-PC.");
            }

            return preferredPort;
        }

        for (var port = FirstVncPort; port <= LastVncPort; port++)
        {
            if (IsPortFree(port))
            {
                return port;
            }
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Escribe en el registro la configuración completa de la sesión antes de arrancar el VNC.
    /// </summary>
    private void ApplyConfiguration(int port, string password, bool viewOnly)
    {
        _userSnapshot = RegistrySnapshot.Capture(
            RegistryHive.CurrentUser, RegistryView.Default, RegistryPath, ManagedValues);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException(
                "No se pudo abrir la configuración de TightVNC en el registro del usuario.");

        var stored = EncryptPassword(password);

        key.SetValue("RfbPort", port, RegistryValueKind.DWord);
        key.SetValue("AcceptRfbConnections", 1, RegistryValueKind.DWord);
        key.SetValue("AcceptHttpConnections", 0, RegistryValueKind.DWord);
        key.SetValue("UseVncAuthentication", 1, RegistryValueKind.DWord);
        key.SetValue("Password", stored, RegistryValueKind.Binary);

        if (viewOnly)
        {
            // Con contraseña de solo lectura el servidor autentica pero descarta la entrada.
            key.SetValue("PasswordViewOnly", stored, RegistryValueKind.Binary);
            key.SetValue("BlockRemoteInput", 1, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("PasswordViewOnly", throwOnMissingValue: false);
            key.SetValue("BlockRemoteInput", 0, RegistryValueKind.DWord);
        }

        // Arreglo del error "Sorry, loopback connections are not enabled": TightVNC rechaza
        // 127.0.0.1 si AllowLoopback no vale 1 (por defecto vale 0). Con LoopbackOnly=1 el
        // RFB únicamente acepta el propio loopback y el túnel del servidor es el único camino.
        key.SetValue("AllowLoopback", 1, RegistryValueKind.DWord);
        key.SetValue("LoopbackOnly", 1, RegistryValueKind.DWord);

        // Fluidez y simplicidad del kiosco: captura D3D, ventanas transparentes, varios espectadores.
        key.SetValue("UseD3D", 1, RegistryValueKind.DWord);
        key.SetValue("GrabTransparentWindows", 1, RegistryValueKind.DWord);
        key.SetValue("AlwaysShared", 1, RegistryValueKind.DWord);
        key.SetValue("NeverShared", 0, RegistryValueKind.DWord);

        // La transferencia de archivos y el applet HTTP quedan fuera del alcance del control remoto.
        key.SetValue("EnableFileTransfers", 0, RegistryValueKind.DWord);
        key.SetValue("RunControlInterface", 0, RegistryValueKind.DWord);
        key.SetValue("IdleTimeout", 0, RegistryValueKind.DWord);
        key.SetValue("ExtraPorts", string.Empty, RegistryValueKind.String);

        _logger.LogInformation(
            "Configuración de TightVNC aplicada (RfbPort={Port}, AllowLoopback=1, LoopbackOnly=1, viewOnly={ViewOnly}).",
            port, viewOnly);

        // Si existe una instalación en modo servicio, su configuración vive en HKLM y también
        // necesita AllowLoopback=1. Se ajusta solo si la clave existe y hay permisos.
        ApplyMachineWideLoopback();
    }

    private static readonly RegistryView[] MachineViews =
    {
        RegistryView.Registry64,
        RegistryView.Registry32
    };

    private void ApplyMachineWideLoopback()
    {
        foreach (var view in MachineViews)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var existing = SafeOpen(root, RegistryPath, writable: false);
                if (existing is null)
                {
                    continue;
                }

                using var writable = SafeOpen(root, RegistryPath, writable: true);
                if (writable is null)
                {
                    _logger.LogDebug(
                        "Sin permisos para ajustar AllowLoopback en HKLM\\{Path} ({View}).", RegistryPath, view);
                    continue;
                }

                var snapshot = RegistrySnapshot.Capture(
                    RegistryHive.LocalMachine, view, RegistryPath, new[] { "AllowLoopback" });
                if (snapshot is not null)
                {
                    _machineSnapshots.Add(snapshot);
                }

                writable.SetValue("AllowLoopback", 1, RegistryValueKind.DWord);
                _logger.LogInformation(
                    "AllowLoopback=1 aplicado a HKLM\\{Path} ({View}) para el modo servicio.", RegistryPath, view);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "No se pudo ajustar AllowLoopback en HKLM ({View}): {Message}", view, ex.Message);
            }
        }
    }

    /// <summary>
    /// Detiene instancias de TightVNC de la misma sesión de usuario que no lanzó este agente.
    /// Devuelve los PID que no pudo cerrar: una instancia huérfana puede mantener el mutex global
    /// de TightVNC y hacer que el proceso nuevo se quede bloqueado mostrando "already running".
    /// </summary>
    private List<int> KillLeftoverInstances()
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        var survivors = new List<int>();

        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                if (candidate.Id == Environment.ProcessId)
                {
                    continue;
                }

                if (candidate.SessionId != currentSession)
                {
                    // Instancia en otra sesión (típicamente el servicio TightVNC en la sesión 0):
                    // no se toca, pero conviene avisar porque su RFB usa la configuración de HKLM.
                    _logger.LogWarning(
                        "Hay un TightVNC en la sesión {Session} (PID {Pid}), probablemente el servicio. " +
                        "El agente usa su propia instancia en el puerto elegido; si el servicio ocupa 5900 " +
                        "se usará el siguiente puerto libre.",
                        candidate.SessionId, candidate.Id);
                    continue;
                }

                _logger.LogWarning(
                    "Deteniendo una instancia previa de TightVNC (PID {Pid}) para aplicar la sesión actual.",
                    candidate.Id);
                candidate.Kill(entireProcessTree: true);
                if (!candidate.WaitForExit(3000))
                {
                    survivors.Add(candidate.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "No se pudo detener la instancia previa de TightVNC (PID {Pid}): {Message}",
                    candidate.Id, ex.Message);
                survivors.Add(candidate.Id);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        if (survivors.Count > 0)
        {
            _logger.LogWarning(
                "Instancias de TightVNC que el agente no puede cerrar: PID {Pids}. " +
                "Si el servidor no arranca, ciérralas manualmente o ejecuta el agente como administrador.",
                string.Join(", ", survivors));
        }

        return survivors;
    }

    private async Task<bool> WaitForListeningAsync(int port, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                return false;
            }

            if (await IsListeningAsync(port, ct))
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        return false;
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Avisa si el RFB quedó escuchando fuera de loopback pese a pedir LoopbackOnly=1.
    /// </summary>
    private void WarnIfExposed(int port)
    {
        try
        {
            var exposed = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && !IPAddress.IsLoopback(endpoint.Address));

            if (exposed)
            {
                _logger.LogWarning(
                    "El RFB de TightVNC escucha en la red local (puerto {Port}). Está protegido por la " +
                    "contraseña de la sesión, pero revisa la opción 'Only allow loopback connections'.",
                    port);
            }
        }
        catch
        {
            // Diagnóstico best-effort.
        }
    }

    /// <summary>
    /// Comprueba, antes de abrir el túnel, que el VNC recién arrancado acepta la contraseña de la
    /// sesión. Autentica como lo hará el panel (RFB 3.8 + VNC auth) y convierte un fallo silencioso
    /// en un mensaje accionable para el operador.
    /// </summary>
    private async Task VerifySessionPasswordAsync(int port, string password, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var stream = client.GetStream();

            var banner = await ReadExactAsync(stream, 12, timeout.Token);
            if (!Encoding.ASCII.GetString(banner).StartsWith("RFB ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"El puerto {port} no respondió como servidor VNC. Revisa si otro programa lo ocupa.");
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), timeout.Token);

            var typesCount = (await ReadExactAsync(stream, 1, timeout.Token))[0];
            if (typesCount == 0)
            {
                // TightVNC rechazó la conexión antes de ofrecer tipos de autenticación.
                throw new InvalidOperationException(
                    $"TightVNC rechazó la conexión local en el puerto {port}: " +
                    $"{await ReadReasonAsync(stream, timeout.Token)}");
            }

            var types = await ReadExactAsync(stream, typesCount, timeout.Token);
            if (Array.IndexOf(types, (byte)2) < 0)
            {
                throw new InvalidOperationException(
                    $"El VNC del puerto {port} no ofrece autenticación por contraseña " +
                    $"(tipos: {string.Join(',', types)}). Activa 'Require VNC authentication' en TightVNC.");
            }

            await stream.WriteAsync(new[] { (byte)2 }, timeout.Token);
            var challenge = await ReadExactAsync(stream, 16, timeout.Token);

            using var des = DES.Create();
            des.Key = BuildDesKey(password);
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            using var encryptor = des.CreateEncryptor();
            var response = encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
            await stream.WriteAsync(response, timeout.Token);

            var status = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, timeout.Token));
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"TightVNC rechazó la contraseña de la sesión en el puerto {port}. " +
                    $"{await ReadReasonAsync(stream, timeout.Token)}");
            }

            _logger.LogInformation(
                "Verificación RFB correcta: el puerto {Port} acepta la contraseña de la sesión.", port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"El VNC del puerto {port} no completó el handshake en 5 s.");
        }
    }

    private static async Task<string> ReadReasonAsync(NetworkStream stream, CancellationToken ct)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, ct));
        if (length == 0 || length > 512)
        {
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(await ReadExactAsync(stream, (int)length, ct));
        return DescribeReject(text);
    }

    private static string DescribeReject(string reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? "sin motivo" : reason.Trim();
        var hint = detail.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            ? " Falta AllowLoopback=1 en la configuración de TightVNC (el agente lo escribe en HKCU; " +
              "si hay un servicio TightVNC activo, deténlo: usa la configuración de HKLM)."
            : detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                ? " TightVNC no está usando la contraseña escrita por el agente: suele pasar cuando el " +
                  "servicio TightVNC está activo o queda una instancia anterior abierta."
                : string.Empty;

        return $"Motivo: {detail}.{hint}";
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (chunk <= 0)
            {
                throw new InvalidOperationException("La conexión VNC se cerró durante la verificación.");
            }

            read += chunk;
        }

        return buffer;
    }

    private static Task<string> CaptureOutputAsync(Process process) => Task.Run(async () =>
    {
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            var text = string.Join(' ', new[] { stdout, stderr }).Trim();
            return text.Length > 400 ? text[..400] : text;
        }
        catch
        {
            return string.Empty;
        }
    });

    private static async Task<string> SafeReadAsync(Task<string> output)
    {
        try
        {
            return (await output).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static RegistryKey? SafeOpen(RegistryKey root, string path, bool writable)
    {
        try
        {
            return root.OpenSubKey(path, writable);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ofusca la contraseña con el esquema de almacenamiento de VNC: DES en modo ECB con la clave
    /// fija del protocolo (con los bits invertidos) sobre la contraseña rellenada a 8 bytes.
    /// Es el formato que TightVNC lee de la clave "Password" del registro; la contraseña se guarda
    /// sin invertir bits porque la inversión ya la aplica la propia clave de almacenamiento.
    /// </summary>
    private static byte[] EncryptPassword(string password)
    {
        var plain = PadPassword(password);

        using var des = DES.Create();
        des.Key = VncStorageKey;
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;

        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    /// <summary>Contraseña rellenada a 8 bytes con ceros, tal como la almacena TightVNC.</summary>
    private static byte[] PadPassword(string password)
    {
        var padded = new byte[8];
        var source = Encoding.ASCII.GetBytes(password ?? string.Empty);
        Array.Copy(source, padded, Math.Min(8, source.Length));
        return padded;
    }

    /// <summary>
    /// Clave DES del cliente RFB: contraseña rellenada a 8 bytes con los bits invertidos.
    /// Con esta clave el cliente cifra el desafío de 16 bytes del servidor.
    /// </summary>
    private static byte[] BuildDesKey(string password)
    {
        var key = PadPassword(password);

        for (var i = 0; i < key.Length; i++)
        {
            key[i] = ReverseBits(key[i]);
        }

        return key;
    }

    private static byte ReverseBits(byte value)
    {
        byte result = 0;
        for (var i = 0; i < 8; i++)
        {
            result = (byte)((result << 1) | (value & 1));
            value >>= 1;
        }

        return result;
    }

    /// <summary>
    /// Copia de seguridad de los valores de TightVNC que el agente modifica, para restaurarlos al cerrar.
    /// </summary>
    private sealed class RegistrySnapshot
    {
        private readonly RegistryHive _hive;
        private readonly RegistryView _view;
        private readonly string _path;
        private readonly Dictionary<string, object?> _values;

        private RegistrySnapshot(
            RegistryHive hive, RegistryView view, string path, Dictionary<string, object?> values)
        {
            _hive = hive;
            _view = view;
            _path = path;
            _values = values;
        }

        public static RegistrySnapshot? Capture(
            RegistryHive hive, RegistryView view, string path, IReadOnlyCollection<string> names)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = SafeOpen(root, path, writable: false);
            if (key is null)
            {
                return null;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }

            return new RegistrySnapshot(hive, view, path, values);
        }

        public void Restore()
        {
            using var root = RegistryKey.OpenBaseKey(_hive, _view);
            using var key = root.CreateSubKey(_path, writable: true);
            if (key == null)
            {
                return;
            }

            foreach (var (name, value) in _values)
            {
                if (value is null)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(name, value);
                }
            }
        }
    }
}
