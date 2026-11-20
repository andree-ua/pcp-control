using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pcp.Agent.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Abre túneles de escritorio remoto: arranca el VNC local, sale hacia el servidor por
/// WebSocket y conecta ese túnel con 127.0.0.1:puerto. El agente no abre puertos entrantes.
/// </summary>
public sealed class VncRemoteControlService : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly VncServerManager _vnc;
    private readonly IConfiguration _config;
    private readonly ILogger<VncRemoteControlService> _logger;
    private readonly ConcurrentDictionary<string, VncTunnel> _tunnels = new();

    private Func<VncTunnelStatusDto, Task>? _statusReporter;

    /// <summary>
    /// Control del kiosco. Mientras el túnel está abierto el reproductor se mantiene
    /// minimizado para que el operador pueda navegar por el escritorio de Windows.
    /// </summary>
    public IKioskWindowController? Kiosk { get; set; }

    public VncRemoteControlService(
        VncServerManager vnc,
        IConfiguration config,
        ILogger<VncRemoteControlService> logger)
    {
        _vnc = vnc;
        _config = config;
        _logger = logger;
    }

    /// <summary>Indica si la mini-PC puede abrir una sesión de control remoto.</summary>
    public bool IsAvailable => _vnc.IsAvailable;

    /// <summary>Motivo por el que el control remoto no está disponible.</summary>
    public string AvailabilityDetail => _vnc.AvailabilityDetail;

    /// <summary>Registra la función con la que se reporta el estado al servidor (SignalR).</summary>
    public void SetStatusReporter(Func<VncTunnelStatusDto, Task> reporter)
    {
        _statusReporter = reporter;
    }

    /// <summary>Lanza el túnel en segundo plano; el resultado se reporta al servidor.</summary>
    public void Start(VncTunnelRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        if (_tunnels.ContainsKey(request.SessionId))
        {
            return;
        }

        var tunnel = new VncTunnel();
        if (!_tunnels.TryAdd(request.SessionId, tunnel))
        {
            tunnel.Cts.Dispose();
            return;
        }

        // Liberar el escritorio antes de arrancar TightVNC: cuando el operador se
        // conecte, el kiosco ya está minimizado y sin TopMost.
        SuspendKiosk();

        _ = Task.Run(() => RunAsync(request, tunnel));
    }

    /// <summary>Cierra un túnel concreto (orden CloseVncTunnel del servidor).</summary>
    public void Stop(string sessionId)
    {
        if (_tunnels.TryRemove(sessionId, out var tunnel))
        {
            _logger.LogInformation("Cerrando túnel VNC {SessionId} por orden del servidor", sessionId);
            tunnel.Cts.Cancel();
        }
    }

    /// <summary>Pide al reproductor que suelte el escritorio mientras dure la sesión.</summary>
    private void SuspendKiosk()
    {
        try
        {
            Kiosk?.Suspend(VlcPlayerManager.VncSuspendReason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo suspender el kiosco para el control remoto: {Message}", ex.Message);
        }
    }

    /// <summary>Restaura el kiosco cuando el túnel ya no existe.</summary>
    private void ResumeKiosk()
    {
        try
        {
            Kiosk?.Resume(VlcPlayerManager.VncSuspendReason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo restaurar el kiosco tras el control remoto: {Message}", ex.Message);
        }
    }

    private async Task RunAsync(VncTunnelRequestDto request, VncTunnel tunnel)
    {
        var ct = tunnel.Cts.Token;
        ClientWebSocket? socket = null;
        TcpClient? tcp = null;
        var reportedSuccess = false;
        var vncStarted = false;

        try
        {
            var port = await _vnc.StartAsync(request.Password, request.PreferredPort, request.ViewOnly, ct);
            vncStarted = true;

            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            var tunnelUri = BuildTunnelUri(request.TunnelPath);
            _logger.LogInformation("Conectando túnel VNC {SessionId} a {Uri}", request.SessionId, tunnelUri);
            await socket.ConnectAsync(tunnelUri, ct);

            tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = tcp.GetStream();

            reportedSuccess = true;
            await ReportAsync(new VncTunnelStatusDto
            {
                SessionId = request.SessionId,
                ScreenId = request.ScreenId,
                Success = true,
                Port = port,
                Detail = "Túnel VNC activo."
            });

            AgentLog.Info($"[VNC] Sesión {request.SessionId} activa (127.0.0.1:{port})");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wsToTcp = PumpWsToTcpAsync(socket, stream, linked.Token);
            var tcpToWs = PumpTcpToWsAsync(stream, socket, linked.Token);
            await Task.WhenAny(wsToTcp, tcpToWs);
            linked.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Cierre solicitado por el servidor o desconexión del hub.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Túnel VNC {SessionId} fallido: {Message}", request.SessionId, ex.Message);

            if (!reportedSuccess)
            {
                await ReportAsync(new VncTunnelStatusDto
                {
                    SessionId = request.SessionId,
                    ScreenId = request.ScreenId,
                    Success = false,
                    Detail = ex.Message
                });
            }
        }
        finally
        {
            try { socket?.Abort(); } catch { }
            socket?.Dispose();
            tcp?.Dispose();

            if (vncStarted)
            {
                await _vnc.StopAsync();
            }

            // El kiosco solo se restaura cuando el túnel terminó de verdad (éxito,
            // caída del socket o fallo de arranque de TightVNC).
            ResumeKiosk();

            _tunnels.TryRemove(request.SessionId, out _);
            tunnel.Cts.Dispose();

            AgentLog.Info($"[VNC] Sesión {request.SessionId} finalizada");
        }
    }

    private async Task ReportAsync(VncTunnelStatusDto status)
    {
        var reporter = _statusReporter;
        if (reporter == null)
        {
            return;
        }

        try
        {
            await reporter(status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudo reportar el estado del túnel VNC: {Message}", ex.Message);
        }
    }

    private Uri BuildTunnelUri(string tunnelPath)
    {
        var serverUrl = _config["Agent:ServerUrl"] ?? "http://localhost:5000";
        var baseUri = new Uri(serverUrl.TrimEnd('/'));
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
        };

        var authority = builder.Uri.GetLeftPart(UriPartial.Authority);
        return new Uri($"{authority}{tunnelPath}");
    }

    private static async Task PumpWsToTcpAsync(WebSocket socket, NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.Count > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpTcpToWsAsync(NetworkStream stream, WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    return;
                }

                await socket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            tunnel.Cts.Cancel();
        }

        _tunnels.Clear();
    }

    /// <summary>
    /// Cancela todos los túneles y detiene el servidor VNC (apagado ordenado del agente).
    /// </summary>
    public async Task StopAllAsync()
    {
        Dispose();
        await _vnc.StopAsync();
    }

    private sealed class VncTunnel
    {
        public CancellationTokenSource Cts { get; } = new();
    }
}
