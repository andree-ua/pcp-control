using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Hubs;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// API REST que abre y cierra sesiones de escritorio remoto (VNC sobre WebSocket).
/// El servidor solo hace de puente: no guarda imágenes ni estado entre sesiones.
/// </summary>
[ApiController]
[Authorize(Roles = TokenService.AdminRole)]
[Route("api/screens/{screenId}/remote")]
public class RemoteControlController : ControllerBase
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(25);

    private readonly AppDbContext _db;
    private readonly IScreenConnectionManager _connections;
    private readonly IVncSessionManager _sessions;
    private readonly IHubContext<ScreenHub> _hub;
    private readonly ILogger<RemoteControlController> _logger;

    public RemoteControlController(
        AppDbContext db,
        IScreenConnectionManager connections,
        IVncSessionManager sessions,
        IHubContext<ScreenHub> hub,
        ILogger<RemoteControlController> logger)
    {
        _db = db;
        _connections = connections;
        _sessions = sessions;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/screens/{screenId}/remote/start
    /// Pide al agente que abra su servidor VNC y el túnel; devuelve la URL WebSocket y la contraseña RFB.
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start(string screenId, [FromBody] StartRemoteControlRequest? request)
    {
        var screen = await _db.Screens
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == screenId, HttpContext.RequestAborted);

        if (screen == null)
        {
            return NotFound(new { error = $"Pantalla '{screenId}' no encontrada." });
        }

        if (!string.Equals(screen.Status, "online", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = $"La pantalla '{screenId}' está desconectada." });
        }

        var connectionId = _connections.GetConnectionId(screenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return BadRequest(new { error = $"La pantalla '{screenId}' no tiene una conexión activa con el servidor." });
        }

        VncSession session;
        try
        {
            session = _sessions.Create(screenId, request?.ViewOnly ?? false);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        var tunnelPath = $"/ws/vnc-agent/{session.Id}" +
                         $"?secret={Uri.EscapeDataString(session.AgentSecret)}" +
                         $"&screenId={Uri.EscapeDataString(screenId)}";

        var request2Agent = new VncTunnelRequestDto
        {
            SessionId = session.Id,
            ScreenId = screenId,
            TunnelPath = tunnelPath,
            Password = session.Password,
            PreferredPort = 0,
            ViewOnly = session.ViewOnly
        };

        try
        {
            await _hub.Clients.Client(connectionId)
                .SendAsync(RemoteControlEvents.OpenTunnel, request2Agent, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _sessions.Close(session.Id, "No se pudo enviar la orden de túnel al agente.");
            _logger.LogWarning(ex, "Fallo al solicitar el túnel VNC a '{ScreenId}'", screenId);
            return StatusCode(500, new { error = $"No se pudo contactar al agente: {ex.Message}" });
        }

        VncTunnelStatusDto status;
        try
        {
            status = await session.AgentStatus.Task.WaitAsync(AgentTimeout, HttpContext.RequestAborted);
        }
        catch (TimeoutException)
        {
            _sessions.Close(session.Id, "El agente no confirmó el túnel VNC a tiempo.");
            return StatusCode(504, new
            {
                error = "El agente no confirmó la apertura del túnel VNC. Revisa que TightVNC esté disponible en la mini-PC."
            });
        }
        catch (OperationCanceledException)
        {
            _sessions.Close(session.Id, "El panel canceló la solicitud de control remoto.");
            return StatusCode(499, new { error = "Solicitud cancelada por el panel." });
        }

        if (!status.Success)
        {
            _sessions.Close(session.Id, status.Detail ?? "El agente rechazó el túnel VNC.");
            return StatusCode(502, new { error = status.Detail ?? "El agente no pudo abrir el servidor VNC." });
        }

        var scheme = Request.IsHttps ? "wss" : "ws";
        var webSocketUrl = $"{scheme}://{Request.Host}{Request.PathBase}/ws/vnc/{session.Id}";

        _logger.LogInformation("Sesión VNC {SessionId} lista para '{ScreenId}'", session.Id, screenId);

        return Ok(new RemoteSessionResponse
        {
            SessionId = session.Id,
            ScreenId = screenId,
            WebSocketUrl = webSocketUrl,
            Password = session.Password,
            ViewOnly = session.ViewOnly,
            ExpiresInSeconds = (int)Math.Max(5, (session.ExpiresAt - DateTime.UtcNow).TotalSeconds)
        });
    }

    /// <summary>
    /// POST /api/screens/{screenId}/remote/stop
    /// Cierra la sesión indicada (o la que tenga abierta la pantalla) y libera el VNC del agente.
    /// </summary>
    [HttpPost("stop")]
    public IActionResult Stop(string screenId, [FromBody] StopRemoteControlRequest? request)
    {
        var session = _sessions.GetByScreen(screenId);
        if (session == null)
        {
            return NoContent();
        }

        if (!string.IsNullOrEmpty(request?.SessionId) &&
            !string.Equals(request.SessionId, session.Id, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "La sesión indicada no pertenece a esta pantalla." });
        }

        _sessions.Close(session.Id, "El operador cerró la sesión de control remoto.");
        return Ok(new { screenId, sessionId = session.Id, status = "closed" });
    }
}

/// <summary>Cuerpo del inicio de sesión de control remoto.</summary>
public class StartRemoteControlRequest
{
    /// <summary>Si es true, el agente abre el VNC en modo solo visualización.</summary>
    public bool ViewOnly { get; set; }
}

/// <summary>Cuerpo del cierre de sesión de control remoto.</summary>
public class StopRemoteControlRequest
{
    /// <summary>Sesión a cerrar. Si se omite se cierra la que esté activa en la pantalla.</summary>
    public string? SessionId { get; set; }
}
