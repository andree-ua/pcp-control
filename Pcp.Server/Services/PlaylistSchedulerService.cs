using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;

namespace Pcp.Server.Services;

/// <summary>
/// Programador de playlists (Bloque 4). Cada pocos segundos busca horarios
/// activos cuya ventana esté por comenzar y lanza la playlist en el destino.
///
/// Detalles que evitan duplicados y sustos:
///   - La hora del horario es local (<c>Scheduling:TimeZone</c>) y se convierte a
///     un instante UTC absoluto con el que arrancan todos los agentes.
///   - Se dispara <c>Scheduling:LeadSeconds</c> antes del inicio para que el
///     comando llegue a tiempo; el agente espera al instante exacto.
///   - <c>LastTriggeredSlot</c> persiste el hueco ya ejecutado: un reinicio del
///     servidor a mitad de ventana no relanza la playlist.
///   - Si no hay ninguna pantalla online, se reintenta en cada tick hasta que la
///     ventana termine (una pantalla puede encenderse tarde).
/// </summary>
public class PlaylistSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaylistSchedulerService> _logger;

    private bool _enabled;
    private TimeSpan _tick;
    private int _leadSeconds;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;

    public PlaylistSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PlaylistSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _enabled = _configuration.GetValue("Scheduling:Enabled", true);
        _tick = TimeSpan.FromSeconds(
            Math.Clamp(_configuration.GetValue("Scheduling:TickSeconds", 20), 5, 300));
        _leadSeconds = Math.Clamp(
            _configuration.GetValue("Scheduling:LeadSeconds", 10),
            PlaylistPlaybackService.MinLeadSeconds,
            PlaylistPlaybackService.MaxLeadSeconds);
        _timeZone = ScheduleTime.ResolveTimeZone(_configuration["Scheduling:TimeZone"]);

        if (!_enabled)
        {
            _logger.LogInformation("Programador de playlists deshabilitado (Scheduling:Enabled=false).");
            return;
        }

        _logger.LogInformation(
            "Programador de playlists activo: zona '{TimeZone}', tick {Tick}s, antelación {Lead}s.",
            _timeZone.Id, _tick.TotalSeconds, _leadSeconds);

        // Margen para que la base de datos termine de migrar en el arranque en frío.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error comprobando horarios de playlists.");
            }

            try
            {
                await Task.Delay(_tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Una pasada del programador; separada para poder probarla con un scope propio.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var playback = scope.ServiceProvider.GetRequiredService<PlaylistPlaybackService>();

        var nowUtc = DateTime.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _timeZone);

        var schedules = await db.Schedules
            .Include(schedule => schedule.Playlist)
            .ThenInclude(playlist => playlist!.Items)
            .Where(schedule => schedule.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            if (schedule.Playlist == null)
            {
                continue;
            }

            if (!ScheduleTime.IsDayEnabled(schedule.DaysOfWeek, localNow.DayOfWeek) ||
                !ScheduleTime.TryParseStartTime(schedule.StartTime, out var start))
            {
                continue;
            }

            var startLocal = localNow.Date + start;
            var slotKey = $"{startLocal:yyyyMMdd}-{start:hh\\:mm}";

            if (string.Equals(schedule.LastTriggeredSlot, slotKey, StringComparison.Ordinal))
            {
                continue;
            }

            // Ventana: desde la antelación hasta el final de la duración planificada.
            var earliest = startLocal - TimeSpan.FromSeconds(_leadSeconds);
            if (localNow < earliest)
            {
                continue;
            }

            var windowMinutes = schedule.DurationMinutes > 0 ? schedule.DurationMinutes : 30;
            if (localNow >= startLocal + TimeSpan.FromMinutes(windowMinutes))
            {
                continue;
            }

            // Instante absoluto del inicio del hueco (reloj del servidor).
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified),
                _timeZone);

            var targetScreens = string.IsNullOrWhiteSpace(schedule.ScreenId)
                ? null
                : new[] { schedule.ScreenId };

            var result = await playback.DispatchAsync(
                schedule.Playlist,
                targetScreens,
                schedule.GroupId,
                startUtc,
                cancellationToken);

            if (result.Success && result.Response is { Sent: > 0 })
            {
                schedule.LastTriggeredSlot = slotKey;
                schedule.LastTriggeredAt = nowUtc;
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Horario '{Schedule}' ejecutado a las {StartLocal:HH:mm} ({TimeZone}): " +
                    "playlist '{Playlist}' en {Sent}/{Total} pantalla(s).",
                    schedule.Name, startLocal, _timeZone.Id,
                    schedule.Playlist.Name, result.Response.Sent, result.Response.Total);
            }
            else if (result.Success)
            {
                // Todas offline: se reintenta en el siguiente tick dentro de la ventana.
                _logger.LogDebug(
                    "Horario '{Schedule}': ninguna pantalla online todavía " +
                    "({Offline} offline, {Failed} fallidas).",
                    schedule.Name, result.Response?.Offline ?? 0, result.Response?.Failed ?? 0);
            }
            else
            {
                _logger.LogWarning(
                    "Horario '{Schedule}' no pudo ejecutarse: {Error}",
                    schedule.Name, result.Error);
            }
        }
    }
}
