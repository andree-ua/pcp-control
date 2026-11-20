using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Pcp.Server.Hubs;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Sesión efímera de proyección (Bloque 5). Vive solo en memoria: contiene el
/// token con el que el panel publica por WHIP, el token con el que el agente lee
/// el stream y el estado que reporta el reproductor.
/// </summary>
public sealed class ProjectorSession
{
    /// <summary>Identificador único y efímero de la sesión.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Pantalla que proyecta la emisión.</summary>
    public string ScreenId { get; init; } = string.Empty;

    /// <summary>Ruta del stream en MediaMTX ("pcp/screen-01").</summary>
    public string StreamPath { get; init; } = string.Empty;

    /// <summary>Token exigido por MediaMTX para publicar (acción "publish").</summary>
    public string PublishToken { get; init; } = string.Empty;

    /// <summary>Token exigido por MediaMTX para leer (RTSP/HLS/WebRTC).</summary>
    public string ReadToken { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Caducidad de la sesión. Mientras el panel no confirme la publicación solo
    /// dispone de unos segundos de gracia; al marcar "live" se amplía al máximo.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>True cuando el panel confirmó que el WHIP está publicando.</summary>
    public bool IsLive { get; set; }

    /// <summary>Volumen solicitado al agente (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Caché de red solicitada al agente, en milisegundos.</summary>
    public int BufferMs { get; set; } = 400;

    /// <summary>Etiqueta de la emisión mostrada en la telemetría de la pantalla.</summary>
    public string? Title { get; set; }

    /// <summary>Último detalle reportado por el agente.</summary>
    public string? LastDetail { get; set; }

    /// <summary>Estado del agente para la orden en curso (se renueva en cada intento).</summary>
    public TaskCompletionSource<ProjectorStatusDto>? StatusSource { get; set; }
}

/// <summary>Resultado de autorizar una petición de MediaMTX.</summary>
public sealed record ProjectorAuthorizationResult(bool Allowed, string? Reason)
{
    public static ProjectorAuthorizationResult Allow() => new(true, null);
    public static ProjectorAuthorizationResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Crea, valida y destruye las sesiones de proyección. También responde a las
/// consultas de autorización que MediaMTX hace por cada publicación y lectura.
/// </summary>
public interface IProjectorSessionManager
{
    ProjectorSession Create(string screenId, string streamPath);
    ProjectorSession? Get(string sessionId);
    ProjectorSession? GetByScreen(string screenId);

    /// <summary>Marca la sesión como publicando y devuelve la fuente de estado para la orden al agente.</summary>
    TaskCompletionSource<ProjectorStatusDto>? MarkLive(string sessionId);

    /// <summary>Devuelve la fuente de estado de la orden en curso, si la hay.</summary>
    TaskCompletionSource<ProjectorStatusDto>? GetStatusSource(string sessionId);

    ProjectorAuthorizationResult Authorize(
        string? action,
        string? path,
        IEnumerable<string?> credentials);

    void ReportStatus(ProjectorStatusDto status);

    /// <summary>Sesiones que el panel ya publicó y que deben seguir vigiladas.</summary>
    IReadOnlyList<ProjectorSession> GetLiveSessions();

    void Close(string sessionId, string reason);
    void CloseByScreen(string screenId, string reason);

    /// <summary>
    /// Cierra una sesión por un fallo externo (MediaMTX caído): detiene al agente
    /// y avisa al panel con el motivo para que muestre el error.
    /// </summary>
    void Fail(string sessionId, string reason);
}

/// <summary>
/// Implementación en memoria del gestor de sesiones de proyección.
/// </summary>
public sealed class ProjectorSessionManager : IProjectorSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, ProjectorSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _byScreen = new();
    private readonly IHubContext<ScreenHub> _hub;
    private readonly IScreenConnectionManager _connections;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectorSessionManager> _logger;
    private readonly Timer _sweeper;

    public ProjectorSessionManager(
        IHubContext<ScreenHub> hub,
        IScreenConnectionManager connections,
        IConfiguration configuration,
        ILogger<ProjectorSessionManager> logger)
    {
        _hub = hub;
        _connections = connections;
        _configuration = configuration;
        _logger = logger;
        _sweeper = new Timer(_ => SweepExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public ProjectorSession Create(string screenId, string streamPath)
    {
        var graceSeconds = Math.Clamp(
            _configuration.GetValue("Projector:PublisherGraceSeconds", 300), 60, 1800);

        var session = new ProjectorSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ScreenId = screenId,
            StreamPath = streamPath,
            PublishToken = GenerateToken(),
            ReadToken = GenerateToken(),
            ExpiresAt = DateTime.UtcNow.AddSeconds(graceSeconds)
        };

        if (!_byScreen.TryAdd(screenId, session.Id))
        {
            throw new InvalidOperationException(
                $"La pantalla '{screenId}' ya tiene una sesión de proyección activa. Ciérrala antes de abrir otra.");
        }

        _sessions[session.Id] = session;
        _logger.LogInformation(
            "Sesión de proyección creada: {SessionId} para '{ScreenId}' (ruta {Path})",
            session.Id, screenId, streamPath);

        return session;
    }

    public ProjectorSession? Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public ProjectorSession? GetByScreen(string screenId)
        => _byScreen.TryGetValue(screenId, out var id) ? Get(id) : null;

    public TaskCompletionSource<ProjectorStatusDto>? MarkLive(string sessionId)
    {
        var session = Get(sessionId);
        if (session is null)
        {
            return null;
        }

        var maxHours = Math.Clamp(_configuration.GetValue("Projector:SessionMaxHours", 12), 1, 48);

        session.IsLive = true;
        session.ExpiresAt = DateTime.UtcNow.AddHours(maxHours);

        // Cada llamada a "live" espera una confirmación nueva del agente.
        session.StatusSource = new TaskCompletionSource<ProjectorStatusDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogInformation("Sesión de proyección {SessionId} marcada como LIVE.", session.Id);
        return session.StatusSource;
    }

    public TaskCompletionSource<ProjectorStatusDto>? GetStatusSource(string sessionId)
        => Get(sessionId)?.StatusSource;

    public ProjectorAuthorizationResult Authorize(
        string? action,
        string? path,
        IEnumerable<string?> credentials)
    {
        var normalizedAction = string.IsNullOrWhiteSpace(action)
            ? "read"
            : action.Trim().ToLowerInvariant();

        // La API interna de MediaMTX ya queda excluida en su configuración; se
        // permite explícitamente para no bloquear el panel de control del contenedor.
        if (normalizedAction is "api" or "metrics" or "pprof")
        {
            return ProjectorAuthorizationResult.Allow();
        }

        var normalizedPath = NormalizePath(path);
        var session = _sessions.Values.FirstOrDefault(s =>
            string.Equals(NormalizePath(s.StreamPath), normalizedPath, StringComparison.Ordinal));

        if (session is null)
        {
            _logger.LogDebug(
                "MediaMTX auth rechazada: no hay sesión para la ruta '{Path}' (acción {Action}).",
                path, normalizedAction);
            return ProjectorAuthorizationResult.Deny("Ruta de emisión desconocida.");
        }

        if (DateTime.UtcNow > session.ExpiresAt)
        {
            return ProjectorAuthorizationResult.Deny("La sesión de proyección ha caducado.");
        }

        var expected = normalizedAction == "publish"
            ? session.PublishToken
            : session.ReadToken;

        foreach (var candidate in credentials)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TokenEquals(candidate.Trim(), expected))
            {
                return ProjectorAuthorizationResult.Allow();
            }
        }

        _logger.LogWarning(
            "MediaMTX auth rechazada para '{ScreenId}' ({Action}): token inválido.",
            session.ScreenId, normalizedAction);

        return ProjectorAuthorizationResult.Deny("Token de emisión inválido.");
    }

    public void ReportStatus(ProjectorStatusDto status)
    {
        var session = Get(status.SessionId);
        if (session is null)
        {
            _logger.LogDebug("Estado de proyección tardío para sesión inexistente {SessionId}", status.SessionId);
            return;
        }

        if (!string.Equals(session.ScreenId, status.ScreenId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ProjectorStatus rechazado: la sesión {SessionId} pertenece a '{Owner}' y llegó de '{ScreenId}'.",
                status.SessionId, session.ScreenId, status.ScreenId);
            return;
        }

        session.LastDetail = status.Detail;
        session.StatusSource?.TrySetResult(status);

        if (!status.Success)
        {
            _logger.LogWarning(
                "El agente no pudo abrir la proyección de '{ScreenId}': {Detail}",
                status.ScreenId, status.Detail);
        }
    }

    public IReadOnlyList<ProjectorSession> GetLiveSessions()
        => _sessions.Values.Where(session => session.IsLive).ToArray();

    public void Close(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _byScreen.TryRemove(session.ScreenId, out _);

        session.StatusSource?.TrySetResult(new ProjectorStatusDto
        {
            SessionId = session.Id,
            ScreenId = session.ScreenId,
            Success = false,
            Detail = reason
        });

        // Avisar al agente para que detenga el reproductor y libere el stream.
        NotifyAgentClose(session);

        _logger.LogInformation("Sesión de proyección cerrada: {SessionId} ({Reason})", sessionId, reason);
    }

    public void Fail(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _byScreen.TryRemove(session.ScreenId, out _);

        var status = new ProjectorStatusDto
        {
            SessionId = session.Id,
            ScreenId = session.ScreenId,
            Success = false,
            Detail = reason
        };

        // Resolver la espera de /live (si sigue en curso) y avisar a ambas puntas.
        session.StatusSource?.TrySetResult(status);
        NotifyAgentClose(session);
        NotifyPanelFailure(status);

        _logger.LogWarning(
            "Sesión de proyección {SessionId} cancelada por fallo del servidor de streaming: {Reason}",
            sessionId, reason);
    }

    public void CloseByScreen(string screenId, string reason)
    {
        if (_byScreen.TryGetValue(screenId, out var sessionId))
        {
            Close(sessionId, reason);
        }
    }

    /// <summary>Ordena al agente detener la emisión, si sigue conectado.</summary>
    private void NotifyAgentClose(ProjectorSession session)
    {
        var connectionId = _connections.GetConnectionId(session.ScreenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        try
        {
            _ = _hub.Clients.Client(connectionId)
                .SendAsync(ProjectorEvents.Close, session.Id)
                .ContinueWith(
                    t => _logger.LogDebug(
                        "No se pudo notificar el cierre de la proyección: {Msg}",
                        t.Exception?.Message),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudo notificar el cierre de la proyección: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Retransmite un fallo al panel aunque el agente no lo haya reportado. El
    /// panel ya muestra un toast con <c>Detail</c> al recibir ProjectorStatusReceived.
    /// </summary>
    private void NotifyPanelFailure(ProjectorStatusDto status)
    {
        _ = _hub.Clients.Group("panel")
            .SendAsync(ProjectorEvents.StatusReceived, status)
            .ContinueWith(
                t => _logger.LogDebug(
                    "No se pudo avisar al panel del fallo del proyector: {Msg}",
                    t.Exception?.Message),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Normaliza la ruta para comparar "pcp/screen-01" con "/pcp/screen-01/".
    /// Algunas variantes de MediaMTX incluyen el sufijo del endpoint (/whip, /whep,
    /// /index.m3u8): se recorta para que la autorización no dependa de la versión.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var value = (path ?? string.Empty).Trim().Trim('/');

        foreach (var suffix in new[] { "/whip", "/whep", "/index.m3u8" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length];
                break;
            }
        }

        return value.Trim('/');
    }

    /// <summary>
    /// Construye la ruta del stream saneando el identificador de pantalla: MediaMTX
    /// solo admite caracteres seguros en la ruta y el nombre debe ser estable.
    /// </summary>
    public static string BuildStreamPath(string? prefix, string screenId)
    {
        var cleanPrefix = string.IsNullOrWhiteSpace(prefix) ? "pcp" : prefix.Trim().Trim('/');
        var cleanId = new string((screenId ?? string.Empty)
            .Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray());

        if (string.IsNullOrEmpty(cleanId))
        {
            cleanId = "screen";
        }

        return $"{cleanPrefix}/{cleanId}";
    }

    private void SweepExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            try
            {
                if (now <= session.ExpiresAt)
                {
                    continue;
                }

                Close(session.Id, session.IsLive
                    ? "La sesión de proyección superó su tiempo máximo."
                    : "El panel no empezó a publicar a tiempo.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Error al limpiar la sesión de proyección {SessionId}: {Message}",
                    session.Id, ex.Message);
            }
        }
    }

    private static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static bool TokenEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
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
