using System.Net.Sockets;

namespace Pcp.Server.Services;

/// <summary>
/// Resultado de comprobar si el servidor de streaming (MediaMTX) está disponible.
/// </summary>
public sealed record ProjectorAvailability(bool Available, string? Detail)
{
    public static ProjectorAvailability Up() => new(true, null);
    public static ProjectorAvailability Down(string detail) => new(false, detail);
}

/// <summary>
/// Comprueba que MediaMTX sea alcanzable desde el servidor antes de ordenar una
/// emisión y mientras hay sesiones en directo. Pcp.Server no transporta vídeo:
/// esta sonda solo evita dejar al agente reintentando contra una IP apagada o
/// incorrecta cuando el servidor de streaming no está disponible.
/// </summary>
public interface IProjectorHealthService
{
    /// <summary>True si las comprobaciones están habilitadas por configuración.</summary>
    bool Enabled { get; }

    /// <summary>Comprueba WHIP (HTTP) y, opcionalmente, RTSP (TCP).</summary>
    Task<ProjectorAvailability> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Invalida el resultado cacheado para forzar una sonda nueva.</summary>
    void Invalidate();
}

/// <summary>
/// Implementación con sonda HTTP al endpoint público de WHIP y sonda TCP al
/// RTSP. Ambas rutas son las que usan el navegador y el agente, así que un fallo
/// aquí explica un proyector que se queda en negro.
/// </summary>
public sealed class ProjectorHealthService : IProjectorHealthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectorHealthService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    private ProjectorAvailability _last = ProjectorAvailability.Up();
    private DateTime _lastCheckedAt = DateTime.MinValue;

    public ProjectorHealthService(
        IConfiguration configuration,
        ILogger<ProjectorHealthService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // El timeout se controla con un CancellationToken por sonda para poder
        // aplicar el valor configurado sin recrear el cliente.
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public bool Enabled => _configuration.GetValue("Projector:HealthCheckEnabled", true);

    public async Task<ProjectorAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return ProjectorAvailability.Up();
        }

        var cacheSeconds = Math.Clamp(
            _configuration.GetValue("Projector:HealthCheckCacheSeconds", 5), 1, 60);
        var cache = TimeSpan.FromSeconds(cacheSeconds);

        if (DateTime.UtcNow - _lastCheckedAt < cache)
        {
            return _last;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Otra petición pudo refrescar el resultado mientras esperábamos.
            if (DateTime.UtcNow - _lastCheckedAt < cache)
            {
                return _last;
            }

            var result = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _last = result;
            _lastCheckedAt = DateTime.UtcNow;

            if (result.Available)
            {
                _logger.LogDebug("MediaMTX disponible para el proyector.");
            }
            else
            {
                _logger.LogWarning("MediaMTX no disponible: {Detail}", result.Detail);
            }

            return result;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    public void Invalidate()
    {
        _lastCheckedAt = DateTime.MinValue;
    }

    private async Task<ProjectorAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(
            _configuration.GetValue("Projector:HealthCheckTimeoutSeconds", 4), 1, 15);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var httpUrl = FirstNonEmpty(
            _configuration["Projector:HealthCheckUrl"],
            _configuration["Projector:PublicWhipBaseUrl"],
            "http://localhost:8889")!;

        var httpError = await ProbeHttpAsync(httpUrl, timeout.Token).ConfigureAwait(false);
        if (httpError is not null)
        {
            return ProjectorAvailability.Down(
                $"No se pudo contactar con {httpUrl}: {httpError}");
        }

        if (_configuration.GetValue("Projector:HealthCheckRtsp", true))
        {
            var rtspUrl = FirstNonEmpty(
                _configuration["Projector:PublicRtspUrl"],
                "rtsp://localhost:8554")!;

            var rtspError = await ProbeRtspAsync(rtspUrl, timeout.Token).ConfigureAwait(false);
            if (rtspError is not null)
            {
                return ProjectorAvailability.Down(
                    $"No se pudo abrir el RTSP {rtspUrl}: {rtspError}");
            }
        }

        return ProjectorAvailability.Up();
    }

    private async Task<string?> ProbeHttpAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "la URL de WHIP no es válida";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // Cualquier respuesta HTTP demuestra que el servidor está vivo y que
            // el puerto es alcanzable (404/401 también cuentan), salvo los
            // errores de pasarela: Caddy responde 502/503/504 si MediaMTX no está.
            var status = (int)response.StatusCode;
            if (status is 502 or 503 or 504)
            {
                return $"el proxy respondió HTTP {status}";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return "tiempo de espera agotado";
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    private static async Task<string?> ProbeRtspAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "la URL de RTSP no es válida";
        }

        var port = uri.IsDefaultPort
            ? (uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase) ? 322 : 8554)
            : uri.Port;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, port, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "tiempo de espera agotado";
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
