using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Pcp.Server.Hubs;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Sesión efímera de control remoto. Vive solo en memoria: contiene la contraseña RFB,
/// el secreto con el que el agente reclama su extremo del túnel y los dos WebSockets
/// (panel y agente) que el servidor pone en comunicación directa.
/// </summary>
public sealed class VncSession
{
    public string Id { get; init; } = string.Empty;
    public string ScreenId { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string AgentSecret { get; init; } = string.Empty;
    public bool ViewOnly { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Fecha límite para que el panel abra el WebSocket. Se desactiva al emparejar.</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddSeconds(90);

    /// <summary>Puerto RFB local del agente (solo informativo/diagnóstico).</summary>
    public int Port { get; set; }

    public WebSocket? PanelSocket { get; set; }
    public WebSocket? AgentSocket { get; set; }

    /// <summary>Resultado reportado por el agente al abrir el túnel.</summary>
    public TaskCompletionSource<VncTunnelStatusDto> AgentStatus { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se completa con la tarea del puente en cuanto ambas puntas están conectadas.</summary>
    public TaskCompletionSource<Task> BridgeCreated { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationTokenSource Cts { get; } = new();

    internal object Gate { get; } = new();
    internal bool BridgeStarted { get; set; }
    internal Task BridgeTask { get; set; } = Task.CompletedTask;
}

/// <summary>
/// Crea, empareja y destruye sesiones de control remoto VNC.
/// Actúa como proxy: el panel nunca ve el puerto 5900 del agente, y el agente
/// nunca abre puertos entrantes (su túnel sale hacia el servidor).
/// </summary>
public interface IVncSessionManager
{
    VncSession Create(string screenId, bool viewOnly);
    VncSession? Get(string sessionId);
    VncSession? GetByScreen(string screenId);
    bool TryClaimAgent(VncSession session, string secret);
    Task AttachPanelAsync(VncSession session, WebSocket socket, CancellationToken ct);
    Task AttachAgentAsync(VncSession session, WebSocket socket, CancellationToken ct);
    void ReportStatus(VncTunnelStatusDto status);
    void Close(string sessionId, string reason);
    void CloseByScreen(string screenId, string reason);
}

/// <summary>
/// Implementación en memoria del gestor de sesiones de control remoto.
/// </summary>
public sealed class VncSessionManager : IVncSessionManager, IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const int SessionMaxHours = 12;

    // Caracteres sin ambigüedad: la contraseña RFB se teclea/transmite tal cual.
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private readonly ConcurrentDictionary<string, VncSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _byScreen = new();
    private readonly IHubContext<ScreenHub> _hub;
    private readonly IScreenConnectionManager _connections;
    private readonly ILogger<VncSessionManager> _logger;
    private readonly Timer _sweeper;

    public VncSessionManager(
        IHubContext<ScreenHub> hub,
        IScreenConnectionManager connections,
        ILogger<VncSessionManager> logger)
    {
        _hub = hub;
        _connections = connections;
        _logger = logger;
        _sweeper = new Timer(_ => SweepExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public VncSession Create(string screenId, bool viewOnly)
    {
        var session = new VncSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ScreenId = screenId,
            Password = GeneratePassword(8),
            AgentSecret = Guid.NewGuid().ToString("N"),
            ViewOnly = viewOnly
        };

        if (!_byScreen.TryAdd(screenId, session.Id))
        {
            throw new InvalidOperationException(
                $"La pantalla '{screenId}' ya tiene una sesión de control remoto activa. Ciérrala antes de abrir otra.");
        }

        _sessions[session.Id] = session;
        _logger.LogInformation("Sesión de control remoto creada: {SessionId} para '{ScreenId}' (viewOnly={ViewOnly})",
            session.Id, screenId, viewOnly);
        return session;
    }

    public VncSession? Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public VncSession? GetByScreen(string screenId)
        => _byScreen.TryGetValue(screenId, out var id) ? Get(id) : null;

    public bool TryClaimAgent(VncSession session, string secret)
    {
        var ok = !string.IsNullOrEmpty(secret) &&
                 string.Equals(secret, session.AgentSecret, StringComparison.Ordinal);
        if (!ok)
        {
            _logger.LogWarning("Rechazado túnel VNC con secreto inválido para la sesión {SessionId}", session.Id);
        }

        return ok;
    }

    public async Task AttachPanelAsync(VncSession session, WebSocket socket, CancellationToken ct)
    {
        await AttachAsync(session, socket, isPanel: true, ct);
    }

    public async Task AttachAgentAsync(VncSession session, WebSocket socket, CancellationToken ct)
    {
        await AttachAsync(session, socket, isPanel: false, ct);
    }

    private async Task AttachAsync(VncSession session, WebSocket socket, bool isPanel, CancellationToken ct)
    {
        lock (session.Gate)
        {
            if (isPanel)
            {
                session.PanelSocket = socket;
            }
            else
            {
                session.AgentSocket = socket;
            }

            if (!session.BridgeStarted && session.PanelSocket is not null && session.AgentSocket is not null)
            {
                session.BridgeStarted = true;
                // Una vez emparejados, la sesión deja de caducar por inactividad inicial.
                session.ExpiresAt = DateTime.MaxValue;
                session.BridgeTask = BridgeAsync(session);
                session.BridgeCreated.TrySetResult(session.BridgeTask);
            }
        }

        _logger.LogInformation("Extremo {Side} conectado a la sesión {SessionId}",
            isPanel ? "panel" : "agente", session.Id);

        // El handler HTTP debe permanecer vivo mientras dure el túnel: ASP.NET cierra
        // el WebSocket en cuanto el endpoint retorna.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
        try
        {
            var bridge = await session.BridgeCreated.Task.WaitAsync(linked.Token);
            await bridge.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Sesión cerrada o cliente desconectado: nada que hacer.
        }
    }

    /// <summary>
    /// Bombea bytes en ambas direcciones entre el panel y el túnel del agente.
    /// RFB es un flujo puro: no hace falta reencuadrar mensajes.
    /// </summary>
    private async Task BridgeAsync(VncSession session)
    {
        var panel = session.PanelSocket!;
        var agent = session.AgentSocket!;

        _logger.LogInformation("Túnel VNC activo para '{ScreenId}' (puerto local {Port})", session.ScreenId, session.Port);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
            var panelToAgent = PumpAsync(panel, agent, linked.Token);
            var agentToPanel = PumpAsync(agent, panel, linked.Token);

            await Task.WhenAny(panelToAgent, agentToPanel);
            linked.Cancel();

            // Cerrar el extremo opuesto para que el otro bombeo termine de inmediato.
            await CloseSocketAsync(panel, WebSocketCloseStatus.NormalClosure, "Sesión finalizada");
            await CloseSocketAsync(agent, WebSocketCloseStatus.NormalClosure, "Sesión finalizada");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error en el túnel VNC de '{ScreenId}': {Message}", session.ScreenId, ex.Message);
        }
        finally
        {
            Close(session.Id, "Sesión de control remoto finalizada.");
        }
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!ct.IsCancellationRequested &&
                   from.State == WebSocketState.Open &&
                   to.State == WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.Count > 0)
                {
                    await to.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        WebSocketMessageType.Binary,
                        result.EndOfMessage,
                        ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal de la sesión.
        }
        catch (WebSocketException)
        {
            // El otro extremo se cayó: el puente se cierra desde BridgeAsync.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void ReportStatus(VncTunnelStatusDto status)
    {
        var session = Get(status.SessionId);
        if (session is null)
        {
            _logger.LogDebug("Estado VNC tardío para sesión inexistente {SessionId}", status.SessionId);
            return;
        }

        session.Port = status.Port;
        session.AgentStatus.TrySetResult(status);

        if (!status.Success)
        {
            _logger.LogWarning("El agente no pudo abrir el túnel VNC de '{ScreenId}': {Detail}",
                status.ScreenId, status.Detail);
        }
    }

    public void Close(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _byScreen.TryRemove(session.ScreenId, out _);
        session.Cts.Cancel();

        session.AgentStatus.TrySetResult(new VncTunnelStatusDto
        {
            SessionId = session.Id,
            ScreenId = session.ScreenId,
            Success = false,
            Detail = reason
        });

        _ = CloseSocketAsync(session.PanelSocket, WebSocketCloseStatus.NormalClosure, reason);
        _ = CloseSocketAsync(session.AgentSocket, WebSocketCloseStatus.NormalClosure, reason);

        // Avisar al agente para que detenga el servidor VNC y libere el puerto.
        var connectionId = _connections.GetConnectionId(session.ScreenId);
        if (connectionId is not null)
        {
            try
            {
                _ = _hub.Clients.Client(connectionId)
                    .SendAsync(RemoteControlEvents.CloseTunnel, session.Id)
                    .ContinueWith(
                        t => _logger.LogDebug("No se pudo notificar el cierre del túnel: {Msg}", t.Exception?.Message),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("No se pudo notificar el cierre del túnel: {Msg}", ex.Message);
            }
        }

        // No se libera el CTS: hay handlers esperando su token y el recolector lo recicla.
        _logger.LogInformation("Sesión de control remoto cerrada: {SessionId} ({Reason})", sessionId, reason);
    }

    public void CloseByScreen(string screenId, string reason)
    {
        if (_byScreen.TryGetValue(screenId, out var sessionId))
        {
            Close(sessionId, reason);
        }
    }

    private static async Task CloseSocketAsync(WebSocket? socket, WebSocketCloseStatus status, string description)
    {
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.CloseOutputAsync(status, Truncate(description), cts.Token);
        }
        catch
        {
            try { socket.Abort(); } catch { }
        }
    }

    private static string Truncate(string value)
        => value.Length <= 100 ? value : value[..100];

    /// <summary>
    /// Cierra sesiones que nunca llegaron a emparejarse o que superaron el máximo de vida.
    /// </summary>
    private void SweepExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            try
            {
                if (now > session.ExpiresAt)
                {
                    Close(session.Id, "La sesión expiró sin que el panel se conectara.");
                }
                else if (now - session.CreatedAt > TimeSpan.FromHours(SessionMaxHours))
                {
                    Close(session.Id, "La sesión superó el tiempo máximo de control remoto.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error al limpiar la sesión VNC {SessionId}: {Message}", session.Id, ex.Message);
            }
        }
    }

    private static string GeneratePassword(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = PasswordAlphabet[Random.Shared.Next(PasswordAlphabet.Length)];
        }

        return new string(chars);
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var session in _sessions.Values)
        {
            Close(session.Id, "El servidor se está deteniendo.");
        }
    }
}
