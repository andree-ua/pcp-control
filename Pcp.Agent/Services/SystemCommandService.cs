using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Pcp.Agent.Services;

/// <summary>
/// Ejecuta acciones de sistema de Windows solicitadas remotamente:
/// apagado, reinicio, apagado de monitor y bloqueo de sesión.
/// </summary>
public class SystemCommandService
{
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;
    private const uint HWND_BROADCAST = 0xFFFF;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    private readonly bool _allowSystemCommands;
    private readonly int _delaySeconds;
    private readonly ILogger<SystemCommandService> _logger;

    public SystemCommandService(
        IConfiguration configuration,
        ILogger<SystemCommandService> logger)
    {
        _allowSystemCommands = configuration.GetValue<bool?>("Agent:AllowSystemCommands") ?? true;
        _delaySeconds = Math.Clamp(
            configuration.GetValue<int?>("Agent:SystemCommandDelaySeconds") ?? 5,
            5,
            300);
        _logger = logger;
    }

    public bool IsEnabled => _allowSystemCommands;

    public (bool Success, string Detail) Shutdown()
    {
        if (!_allowSystemCommands)
        {
            return Disabled();
        }

        return RunShutdown("/s", "Apagado");
    }

    public (bool Success, string Detail) Restart()
    {
        if (!_allowSystemCommands)
        {
            return Disabled();
        }

        return RunShutdown("/r", "Reinicio");
    }

    public (bool Success, string Detail) MonitorOff()
    {
        if (!_allowSystemCommands)
        {
            return Disabled();
        }

        try
        {
            var sent = SendMessageTimeout(
                new IntPtr(HWND_BROADCAST),
                WM_SYSCOMMAND,
                new IntPtr(SC_MONITORPOWER),
                new IntPtr(MONITOR_OFF),
                SMTO_ABORTIFHUNG,
                2000,
                out _);

            if (sent == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return (false, $"No se pudo apagar el monitor (error {error}).");
            }

            return (true, "Monitor apagado.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al apagar el monitor");
            return (false, $"Error al apagar el monitor: {ex.Message}");
        }
    }

    public (bool Success, string Detail) LockSession()
    {
        if (!_allowSystemCommands)
        {
            return Disabled();
        }

        try
        {
            if (LockWorkStation())
            {
                return (true, "Sesión de Windows bloqueada.");
            }

            var error = Marshal.GetLastWin32Error();
            return (false, $"No se pudo bloquear la sesión (error {error}).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al bloquear la sesión");
            return (false, $"Error al bloquear la sesión: {ex.Message}");
        }
    }

    private (bool Success, string Detail) RunShutdown(string flag, string action)
    {
        try
        {
            var arguments = $"{flag} /t {_delaySeconds} /c \"PCP: {action} remoto\"";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return (false, "No se pudo iniciar shutdown.exe.");
            }

            // shutdown.exe suele responder rápido; si falla, se informa al panel.
            if (process.WaitForExit(3000) && process.ExitCode != 0)
            {
                return (false, $"{action} rechazado por Windows (código {process.ExitCode}).");
            }

            _logger.LogInformation("{Action} programado en {Delay} segundos", action, _delaySeconds);
            return (true, $"{action} programado en {_delaySeconds} segundos.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al ejecutar {Action}", action);
            return (false, $"Error al ejecutar {action.ToLowerInvariant()}: {ex.Message}");
        }
    }

    private static (bool Success, string Detail) Disabled() =>
        (false, "Los comandos de sistema están deshabilitados en este agente (Agent:AllowSystemCommands=false).");
}
