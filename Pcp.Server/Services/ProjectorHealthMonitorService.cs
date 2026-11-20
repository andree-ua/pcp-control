using System.Collections.Concurrent;

namespace Pcp.Server.Services;

/// <summary>
/// Vigila MediaMTX mientras hay emisiones en directo. Si el servidor de
/// streaming deja de responder (por ejemplo, la instancia de Oracle está
/// apagada o la IP pública cambió), cierra la sesión: el agente recibe
/// <c>CloseProjector</c> y vuelve a idle, y el panel recibe el estado de fallo
/// para mostrar "Servidor de Streaming no disponible".
/// </summary>
public sealed class ProjectorHealthMonitorService : BackgroundService
{
    private readonly IProjectorSessionManager _sessions;
    private readonly IProjectorHealthService _health;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectorHealthMonitorService> _logger;

    private readonly ConcurrentDictionary<string, int> _failures = new();

    public ProjectorHealthMonitorService(
        IProjectorSessionManager sessions,
        IProjectorHealthService health,
        IConfiguration configuration,
        ILogger<ProjectorHealthMonitorService> logger)
    {
        _sessions = sessions;
        _health = health;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_health.Enabled)
        {
            _logger.LogInformation(
                "Monitor del proyector deshabilitado (Projector:HealthCheckEnabled=false).");
            return;
        }

        var intervalSeconds = Math.Clamp(
            _configuration.GetValue("Projector:HealthCheckIntervalSeconds", 20), 5, 300);
        var failuresBeforeClose = Math.Clamp(
            _configuration.GetValue("Projector:HealthCheckFailuresBeforeClose", 2), 1, 10);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                var liveSessions = _sessions.GetLiveSessions();
                if (liveSessions.Count == 0)
                {
                    // Sin emisiones no hay nada que vigilar: se limpia el estado.
                    _failures.Clear();
                    continue;
                }

                // Limpia contadores de sesiones que ya se cerraron.
                var liveIds = liveSessions.Select(session => session.Id).ToHashSet();
                foreach (var staleId in _failures.Keys.Where(id => !liveIds.Contains(id)))
                {
                    _failures.TryRemove(staleId, out _);
                }

                // Una sola sonda sirve para todas las sesiones vivas.
                var availability = await _health.CheckAsync(stoppingToken).ConfigureAwait(false);

                foreach (var session in liveSessions)
                {
                    if (availability.Available)
                    {
                        _failures.TryRemove(session.Id, out _);
                        continue;
                    }

                    var failures = _failures.AddOrUpdate(session.Id, 1, (_, value) => value + 1);
                    _logger.LogWarning(
                        "MediaMTX no responde ({Failures}/{Threshold}) para la sesión {SessionId} de '{ScreenId}'.",
                        failures, failuresBeforeClose, session.Id, session.ScreenId);

                    if (failures < failuresBeforeClose)
                    {
                        continue;
                    }

                    _failures.TryRemove(session.Id, out _);
                    _sessions.Fail(session.Id, "Servidor de Streaming no disponible");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Error en el monitor del proyector: {Message}", ex.Message);
            }
        }
    }
}
