using System.Security.Cryptography;
using System.Text;
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
/// API REST del proyector (Bloque 5). El panel publica su pantalla por WHIP a
/// MediaMTX y aquí se prepara la sesión, se avisa al agente y se cierra la emisión.
/// El servidor no reenvía vídeo: solo coordina tokens, URLs y estado.
/// </summary>
[ApiController]
[Authorize(Roles = TokenService.AdminRole)]
[Route("api/screens/{screenId}/projector")]
public class ProjectorController : ControllerBase
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(20);

    private readonly AppDbContext _db;
    private readonly IScreenConnectionManager _connections;
    private readonly IProjectorSessionManager _sessions;
    private readonly IProjectorHealthService _health;
    private readonly IHubContext<ScreenHub> _hub;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectorController> _logger;

    public ProjectorController(
        AppDbContext db,
        IScreenConnectionManager connections,
        IProjectorSessionManager sessions,
        IProjectorHealthService health,
        IHubContext<ScreenHub> hub,
        IConfiguration configuration,
        ILogger<ProjectorController> logger)
    {
        _db = db;
        _connections = connections;
        _sessions = sessions;
        _health = health;
        _hub = hub;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/screens/{screenId}/projector/start
    /// Prepara una sesión de proyección y devuelve la URL WHIP para publicar.
    /// No ordena nada al agente todavía: el panel avisa con "live" al conectar el WHIP.
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start(string screenId, [FromBody] StartProjectorRequest? request)
    {
        if (!ProjectorEnabled)
        {
            return StatusCode(503, new { error = "El proyector no está habilitado en el servidor (Projector:Enabled)." });
        }

        var screen = await _db.Screens
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == screenId, HttpContext.RequestAborted);

        if (screen == null)
        {
            return NotFound(new { error = $"Pantalla '{screenId}' no encontrada." });
        }

        if (!string.Equals(screen.Status, "online", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(_connections.GetConnectionId(screenId)))
        {
            return BadRequest(new { error = $"La pantalla '{screenId}' está desconectada." });
        }

        if (_sessions.GetByScreen(screenId) is not null)
        {
            return Conflict(new { error = $"La pantalla '{screenId}' ya tiene una emisión activa. Ciérrala antes de abrir otra." });
        }

        // Fail-safe: si MediaMTX no es alcanzable desde el servidor (IP mal,
        // instancia apagada...), no se crea la sesión y el panel muestra el error
        // exacto. El agente permanece en idle. Se fuerza una sonda nueva.
        _health.Invalidate();
        var availability = await _health.CheckAsync(HttpContext.RequestAborted);
        if (!availability.Available)
        {
            _logger.LogWarning(
                "Proyector rechazado para '{ScreenId}': MediaMTX no disponible ({Detail}).",
                screenId, availability.Detail);

            return StatusCode(503, new
            {
                error = "Servidor de Streaming no disponible",
                detail = availability.Detail
            });
        }

        var pathPrefix = _configuration["Projector:PathPrefix"] ?? "pcp";
        var streamPath = ProjectorSessionManager.BuildStreamPath(pathPrefix, screenId);

        ProjectorSession session;
        try
        {
            session = _sessions.Create(screenId, streamPath);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        session.Volume = Math.Clamp(
            request?.Volume ?? _configuration.GetValue("Projector:Volume", 80), 0, 100);
        session.BufferMs = Math.Clamp(
            request?.BufferMs ?? _configuration.GetValue("Projector:BufferMs", 400), 100, 3000);
        session.Title = string.IsNullOrWhiteSpace(request?.Title)
            ? $"{screen.Name} (proyector)"
            : request!.Title!.Trim();

        var readUser = _configuration["Projector:ReadUser"] ?? "pcp";

        var response = new ProjectorSessionResponse
        {
            SessionId = session.Id,
            ScreenId = screenId,
            StreamPath = streamPath,
            WhipUrl = BuildWhipUrl(streamPath, session.PublishToken),
            WhipToken = session.PublishToken,
            RtspUrl = BuildRtspUrl(streamPath, session.ReadToken),
            HlsUrl = BuildHlsUrl(streamPath, readUser, session.ReadToken),
            BufferMs = session.BufferMs,
            Volume = session.Volume,
            ExpiresInSeconds = (int)Math.Max(5, (session.ExpiresAt - DateTime.UtcNow).TotalSeconds)
        };

        _logger.LogInformation(
            "Proyección preparada para '{ScreenId}': sesión {SessionId}, ruta {Path}",
            screenId, session.Id, streamPath);

        return Ok(response);
    }

    /// <summary>
    /// POST /api/screens/{screenId}/projector/live
    /// Confirma que el panel ya está publicando y ordena al agente reproducir el stream.
    /// </summary>
    [HttpPost("live")]
    public async Task<IActionResult> Live(string screenId, [FromBody] ProjectorLiveRequest? request)
    {
        if (!ProjectorEnabled)
        {
            return StatusCode(503, new { error = "El proyector no está habilitado en el servidor (Projector:Enabled)." });
        }

        var session = _sessions.GetByScreen(screenId);
        if (session == null)
        {
            return NotFound(new { error = $"No hay una sesión de proyección activa para '{screenId}'." });
        }

        if (!string.IsNullOrWhiteSpace(request?.SessionId) &&
            !string.Equals(request.SessionId, session.Id, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "La sesión indicada no pertenece a esta pantalla." });
        }

        var connectionId = _connections.GetConnectionId(screenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return BadRequest(new { error = $"La pantalla '{screenId}' perdió la conexión con el servidor." });
        }

        // Fail-safe: aunque el panel haya publicado el WHIP, el agente no debe
        // recibir la orden si el servidor de streaming dejó de responder entre
        // "start" y "live". Se fuerza una sonda nueva (sin caché) y, si falla,
        // se cierra la sesión y se devuelve el motivo.
        _health.Invalidate();
        var availability = await _health.CheckAsync(HttpContext.RequestAborted);
        if (!availability.Available)
        {
            _sessions.Close(session.Id, "Servidor de Streaming no disponible");
            return StatusCode(503, new
            {
                error = "Servidor de Streaming no disponible",
                detail = availability.Detail
            });
        }

        var statusSource = _sessions.MarkLive(session.Id);
        if (statusSource == null)
        {
            return NotFound(new { error = "La sesión de proyección ya no existe." });
        }

        var readUser = _configuration["Projector:ReadUser"] ?? "pcp";
        var rtspUrl = BuildRtspUrl(session.StreamPath, session.ReadToken);
        var payload = new ProjectorCommandPayload
        {
            SessionId = session.Id,
            ScreenId = screenId,
            StreamUrl = rtspUrl,
            FallbackUrl = BuildHlsUrl(session.StreamPath, readUser, session.ReadToken),
            Protocol = ProtocolOf(rtspUrl),
            Volume = session.Volume,
            BufferMs = session.BufferMs,
            Title = session.Title
        };

        try
        {
            await _hub.Clients.Client(connectionId)
                .SendAsync(ProjectorEvents.Open, payload, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo ordenar la proyección a '{ScreenId}'", screenId);
            _sessions.Close(session.Id, "No se pudo contactar al agente.");
            return StatusCode(500, new { error = $"No se pudo contactar al agente: {ex.Message}" });
        }

        ProjectorStatusDto status;
        try
        {
            status = await statusSource.Task.WaitAsync(AgentTimeout, HttpContext.RequestAborted);
        }
        catch (TimeoutException)
        {
            _sessions.Close(session.Id, "El agente no confirmó la reproducción del stream.");
            return StatusCode(504, new
            {
                error = "El agente no confirmó la reproducción del stream. Revisa su conexión con MediaMTX."
            });
        }
        catch (OperationCanceledException)
        {
            _sessions.Close(session.Id, "Solicitud cancelada por el panel.");
            return StatusCode(499, new { error = "Solicitud cancelada por el panel." });
        }

        if (!status.Success)
        {
            _sessions.Close(session.Id, status.Detail ?? "El agente no pudo reproducir la emisión.");
            return StatusCode(502, new { error = status.Detail ?? "El agente no pudo reproducir la emisión." });
        }

        _logger.LogInformation(
            "Proyección LIVE en '{ScreenId}' (sesión {SessionId}): {Detail}",
            screenId, session.Id, status.Detail);

        return Ok(new ProjectorActionResponse
        {
            SessionId = session.Id,
            ScreenId = screenId,
            Status = "live",
            Detail = status.Detail
        });
    }

    /// <summary>
    /// POST /api/screens/{screenId}/projector/stop
    /// Cierra la emisión y ordena al agente detener la reproducción.
    /// </summary>
    [HttpPost("stop")]
    public IActionResult Stop(string screenId, [FromBody] StopProjectorRequest? request)
    {
        var session = !string.IsNullOrWhiteSpace(request?.SessionId)
            ? _sessions.Get(request!.SessionId!)
            : _sessions.GetByScreen(screenId);

        if (session == null)
        {
            return NoContent();
        }

        if (!string.Equals(session.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "La sesión indicada no pertenece a esta pantalla." });
        }

        _sessions.Close(session.Id, "El operador cerró la emisión del proyector.");

        return Ok(new ProjectorActionResponse
        {
            SessionId = session.Id,
            ScreenId = screenId,
            Status = "closed",
            Detail = "Emisión finalizada."
        });
    }

    /// <summary>GET /api/screens/{screenId}/projector: estado actual de la sesión.</summary>
    [HttpGet]
    public IActionResult Status(string screenId)
    {
        var session = _sessions.GetByScreen(screenId);
        if (session == null)
        {
            return Ok(new ProjectorStatusResponse { Active = false, ScreenId = screenId });
        }

        return Ok(new ProjectorStatusResponse
        {
            Active = true,
            SessionId = session.Id,
            ScreenId = screenId,
            StreamPath = session.StreamPath,
            IsLive = session.IsLive,
            Detail = session.LastDetail,
            ExpiresAt = session.ExpiresAt
        });
    }

    private bool ProjectorEnabled => _configuration.GetValue("Projector:Enabled", true);

    private string BuildWhipUrl(string streamPath, string token)
    {
        var baseUrl = ResolveBaseUrl(
            _configuration["Projector:PublicWhipBaseUrl"], "http://localhost:8889");

        return $"{baseUrl}/{streamPath}/whip?token={Uri.EscapeDataString(token)}";
    }

    private string BuildHlsUrl(string streamPath, string readUser, string token)
    {
        var baseUrl = ResolveBaseUrl(
            _configuration["Projector:PublicHlsBaseUrl"], "http://localhost:8888");

        // MediaMTX pide las credenciales de HLS con Authorization: Basic. LibVLC
        // las envía al recibir el reto 401 si van en la propia URL.
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri)
            {
                UserName = readUser,
                Password = token,
                Path = $"{uri.AbsolutePath.TrimEnd('/')}/{streamPath}/index.m3u8"
            };

            return builder.Uri.ToString();
        }

        return $"{baseUrl}/{streamPath}/index.m3u8";
    }

    private string BuildRtspUrl(string streamPath, string token)
    {
        var baseUrl = ResolveBaseUrl(
            _configuration["Projector:PublicRtspUrl"], "rtsp://localhost:8554");

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            // RTSP acepta el token como query (?token=...), que MediaMTX reenvía
            // al endpoint de autorización en la primera petición (sin reto 401).
            var builder = new UriBuilder(uri)
            {
                Path = $"{uri.AbsolutePath.TrimEnd('/')}/{streamPath}",
                Query = $"token={Uri.EscapeDataString(token)}"
            };

            return builder.Uri.ToString();
        }

        return $"{baseUrl}/{streamPath}?token={Uri.EscapeDataString(token)}";
    }

    private static string ResolveBaseUrl(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured)
            ? fallback
            : configured.Trim().TrimEnd('/');

    private static string ProtocolOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Scheme.ToLowerInvariant()
            : "rtsp";
}

/// <summary>Cuerpo opcional del inicio de emisión.</summary>
public class StartProjectorRequest
{
    /// <summary>Volumen en la pantalla destino (0-100). Si se omite se usa Projector:Volume.</summary>
    public int? Volume { get; set; }

    /// <summary>Caché de red del agente en milisegundos (100-3000).</summary>
    public int? BufferMs { get; set; }

    /// <summary>Etiqueta de la emisión (por defecto el nombre de la pantalla).</summary>
    public string? Title { get; set; }
}

/// <summary>Cuerpo de la confirmación de publicación.</summary>
public class ProjectorLiveRequest
{
    /// <summary>Sesión que el panel acaba de publicar.</summary>
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>Cuerpo del cierre de emisión.</summary>
public class StopProjectorRequest
{
    /// <summary>Sesión a cerrar. Si se omite se cierra la activa de la pantalla.</summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// Endpoint de autorización que MediaMTX consulta antes de aceptar una publicación
/// o una lectura. Es anónimo porque lo llama el propio servidor de medios.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/projector")]
public class ProjectorAuthController : ControllerBase
{
    private readonly IProjectorSessionManager _sessions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectorAuthController> _logger;

    public ProjectorAuthController(
        IProjectorSessionManager sessions,
        IConfiguration configuration,
        ILogger<ProjectorAuthController> logger)
    {
        _sessions = sessions;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/projector/auth
    /// MediaMTX envía { user, password, token, ip, action, path, protocol, query }
    /// y espera un 2xx para permitir la operación.
    /// </summary>
    [HttpPost("auth")]
    public IActionResult Auth([FromBody] MediaMtxAuthRequest? request, [FromQuery] string? key)
    {
        if (request == null)
        {
            return Unauthorized(new { error = "Petición de autorización vacía." });
        }

        var expectedKey = _configuration["Projector:AuthKey"];
        if (!string.IsNullOrWhiteSpace(expectedKey) && !FixedTimeEquals(key, expectedKey))
        {
            _logger.LogWarning("MediaMTX auth rechazada: la clave compartida no coincide.");
            return Unauthorized(new { error = "Clave de autorización inválida." });
        }

        var result = _sessions.Authorize(
            request.Action,
            request.Path,
            EnumerateCredentials(request));

        if (!result.Allowed)
        {
            return Unauthorized(new { error = result.Reason ?? "Emisión no autorizada." });
        }

        return Ok(new { allowed = true });
    }

    /// <summary>
    /// Extrae los posibles tokens de la petición de MediaMTX: cabecera Basic
    /// (user/password), Bearer (token) y credenciales por query (WHIP/HLS).
    /// </summary>
    private static IEnumerable<string?> EnumerateCredentials(MediaMtxAuthRequest request)
    {
        yield return request.Token;
        yield return request.Password;

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            yield break;
        }

        var query = request.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator >= 0 ? pair[..separator] : pair;
            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;

            if (name is "token" or "pass" or "password" or "user")
            {
                yield return Uri.UnescapeDataString(value);
            }
        }
    }

    private static bool FixedTimeEquals(string? provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>Cuerpo que MediaMTX envía a authHTTPAddress.</summary>
public class MediaMtxAuthRequest
{
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }
    public string? Ip { get; set; }
    public string? Action { get; set; }
    public string? Path { get; set; }
    public string? Protocol { get; set; }
    public string? Id { get; set; }
    public string? Query { get; set; }
}
