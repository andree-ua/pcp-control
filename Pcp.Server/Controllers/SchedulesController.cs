using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// CRUD y ejecución manual de horarios que programan playlists (Bloque 4).
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Viewer")]
[Route("api/schedules")]
public class SchedulesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PlaylistPlaybackService _playback;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SchedulesController> _logger;

    public SchedulesController(
        AppDbContext db,
        PlaylistPlaybackService playback,
        IConfiguration configuration,
        ILogger<SchedulesController> logger)
    {
        _db = db;
        _playback = playback;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>GET /api/schedules — horarios con nombre de playlist, destino y próxima ejecución.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ScheduleDto>>> GetAll(CancellationToken cancellationToken)
    {
        var schedules = await _db.Schedules
            .AsNoTracking()
            .Include(schedule => schedule.Playlist)
            .Include(schedule => schedule.Screen)
            .Include(schedule => schedule.Group)
            .OrderBy(schedule => schedule.StartTime)
            .ThenBy(schedule => schedule.Name)
            .ToListAsync(cancellationToken);

        var timeZone = ResolveTimeZone();
        var nowUtc = DateTime.UtcNow;

        return Ok(schedules.Select(schedule => ToDto(schedule, timeZone, nowUtc)));
    }

    /// <summary>GET /api/schedules/{id}</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ScheduleDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var schedule = await _db.Schedules
            .AsNoTracking()
            .Include(item => item.Playlist)
            .Include(item => item.Screen)
            .Include(item => item.Group)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (schedule == null)
        {
            return NotFound(new { error = $"Horario '{id}' no encontrado." });
        }

        return Ok(ToDto(schedule, ResolveTimeZone(), DateTime.UtcNow));
    }

    /// <summary>POST /api/schedules — crea un horario recurrente.</summary>
    [HttpPost]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Create(
        [FromBody] ScheduleUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var (error, normalized) = await NormalizeAsync(dto, cancellationToken);
        if (normalized == null)
        {
            return BadRequest(new { error });
        }

        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = normalized.Name,
            PlaylistId = normalized.Playlist.Id,
            ScreenId = normalized.ScreenId,
            GroupId = normalized.GroupId,
            DaysOfWeek = normalized.Days,
            StartTime = normalized.StartTime,
            DurationMinutes = normalized.DurationMinutes,
            Enabled = dto.Enabled,
            CreatedAt = DateTime.UtcNow
        };

        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken);

        await LoadNavigationAsync(schedule, cancellationToken);

        _logger.LogInformation(
            "Horario creado: '{Name}' ({ScheduleId}) → playlist '{Playlist}' los días {Days} a las {StartTime}.",
            schedule.Name, schedule.Id, normalized.Playlist.Name, schedule.DaysOfWeek, schedule.StartTime);

        return Ok(ToDto(schedule, ResolveTimeZone(), DateTime.UtcNow));
    }

    /// <summary>PUT /api/schedules/{id} — actualiza el horario completo.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] ScheduleUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var (error, normalized) = await NormalizeAsync(dto, cancellationToken);
        if (normalized == null)
        {
            return BadRequest(new { error });
        }

        var schedule = await _db.Schedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (schedule == null)
        {
            return NotFound(new { error = $"Horario '{id}' no encontrado." });
        }

        var previousPlaylistId = schedule.PlaylistId;
        var previousScreenId = schedule.ScreenId;
        var previousGroupId = schedule.GroupId;
        var previousDays = schedule.DaysOfWeek;
        var previousStartTime = schedule.StartTime;

        schedule.Name = normalized.Name;
        schedule.PlaylistId = normalized.Playlist.Id;
        schedule.ScreenId = normalized.ScreenId;
        schedule.GroupId = normalized.GroupId;
        schedule.DaysOfWeek = normalized.Days;
        schedule.StartTime = normalized.StartTime;
        schedule.DurationMinutes = normalized.DurationMinutes;
        schedule.Enabled = dto.Enabled;

        // Solo se libera el hueco si cambia la definición del horario (playlist,
        // destino, días u hora). Pausar y reactivar a mitad de ventana no debe
        // relanzar la playlist.
        var definitionChanged =
            previousPlaylistId != normalized.Playlist.Id ||
            !string.Equals(previousScreenId, normalized.ScreenId, StringComparison.OrdinalIgnoreCase) ||
            previousGroupId != normalized.GroupId ||
            !string.Equals(previousDays, normalized.Days, StringComparison.Ordinal) ||
            !string.Equals(previousStartTime, normalized.StartTime, StringComparison.Ordinal);

        if (definitionChanged)
        {
            schedule.LastTriggeredSlot = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await LoadNavigationAsync(schedule, cancellationToken);

        _logger.LogInformation(
            "Horario actualizado: '{Name}' ({ScheduleId}) los días {Days} a las {StartTime}.",
            schedule.Name, schedule.Id, schedule.DaysOfWeek, schedule.StartTime);

        return Ok(ToDto(schedule, ResolveTimeZone(), DateTime.UtcNow));
    }

    /// <summary>DELETE /api/schedules/{id}</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var schedule = await _db.Schedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (schedule == null)
        {
            return NotFound(new { error = $"Horario '{id}' no encontrado." });
        }

        _db.Schedules.Remove(schedule);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Horario eliminado: '{Name}' ({ScheduleId}).", schedule.Name, schedule.Id);

        return NoContent();
    }

    /// <summary>
    /// POST /api/schedules/{id}/run — ejecuta el horario ahora (prueba manual),
    /// con el mismo camino que usa el programador.
    /// </summary>
    [HttpPost("{id:guid}/run")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Run(Guid id, CancellationToken cancellationToken)
    {
        var schedule = await _db.Schedules
            .Include(item => item.Playlist)
            .ThenInclude(playlist => playlist!.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (schedule == null)
        {
            return NotFound(new { error = $"Horario '{id}' no encontrado." });
        }

        if (schedule.Playlist == null)
        {
            return BadRequest(new { error = "El horario apunta a una playlist inexistente." });
        }

        if (string.IsNullOrWhiteSpace(schedule.ScreenId) && schedule.GroupId == null)
        {
            return BadRequest(new { error = "El horario no tiene pantalla ni grupo destino." });
        }

        var targetScreens = string.IsNullOrWhiteSpace(schedule.ScreenId)
            ? null
            : new[] { schedule.ScreenId };

        var result = await _playback.DispatchAsync(
            schedule.Playlist,
            targetScreens,
            schedule.GroupId,
            DateTime.UtcNow.AddSeconds(PlaylistPlaybackService.DefaultLeadSeconds),
            cancellationToken);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                "not-found" => NotFound(new { error = result.Error }),
                _ => BadRequest(new { error = result.Error })
            };
        }

        _logger.LogInformation(
            "Horario '{Name}' ejecutado manualmente: {Sent}/{Total} pantalla(s).",
            schedule.Name, result.Response!.Sent, result.Response.Total);

        return Ok(result.Response);
    }

    private TimeZoneInfo ResolveTimeZone()
        => ScheduleTime.ResolveTimeZone(_configuration["Scheduling:TimeZone"]);

    private async Task LoadNavigationAsync(Schedule schedule, CancellationToken cancellationToken)
    {
        await _db.Entry(schedule).Reference(item => item.Playlist).LoadAsync(cancellationToken);
        await _db.Entry(schedule).Reference(item => item.Screen).LoadAsync(cancellationToken);
        await _db.Entry(schedule).Reference(item => item.Group).LoadAsync(cancellationToken);
    }

    private sealed record NormalizedSchedule(
        Playlist Playlist,
        string? ScreenId,
        Guid? GroupId,
        string Days,
        string StartTime,
        int DurationMinutes,
        string Name);

    private async Task<(string? Error, NormalizedSchedule? Value)> NormalizeAsync(
        ScheduleUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var hasScreen = !string.IsNullOrWhiteSpace(dto.ScreenId);
        var hasGroup = dto.GroupId.HasValue;

        if (hasScreen == hasGroup)
        {
            return ("Indica exactamente una pantalla o un grupo como destino.", null);
        }

        var playlist = await _db.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == dto.PlaylistId, cancellationToken);

        if (playlist == null)
        {
            return ("La playlist indicada no existe.", null);
        }

        if (hasScreen)
        {
            var screenId = dto.ScreenId!.Trim();
            var exists = await _db.Screens.AsNoTracking().AnyAsync(s => s.Id == screenId, cancellationToken);
            if (!exists)
            {
                return ($"Pantalla '{screenId}' no encontrada.", null);
            }

            dto.ScreenId = screenId;
        }
        else
        {
            var exists = await _db.ScreenGroups
                .AsNoTracking()
                .AnyAsync(g => g.Id == dto.GroupId!.Value, cancellationToken);

            if (!exists)
            {
                return ($"Grupo '{dto.GroupId}' no encontrado.", null);
            }
        }

        if (!ScheduleTime.TryParseStartTime(dto.StartTime, out var start))
        {
            return ("La hora de inicio debe tener formato HH:mm (24 h).", null);
        }

        var days = ScheduleTime.FormatDays(dto.DaysOfWeek);
        if (days.Length == 0)
        {
            return ("Selecciona al menos un día de la semana.", null);
        }

        var startTime = ScheduleTime.FormatStartTime(start);
        var name = dto.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{playlist.Name} · {startTime}";
        }

        if (name.Length > 100)
        {
            name = name[..100];
        }

        return (null, new NormalizedSchedule(
            playlist,
            hasScreen ? dto.ScreenId : null,
            hasGroup ? dto.GroupId : null,
            days,
            startTime,
            Math.Clamp(dto.DurationMinutes, 0, 1440),
            name));
    }

    private static ScheduleDto ToDto(Schedule schedule, TimeZoneInfo timeZone, DateTime nowUtc) => new()
    {
        Id = schedule.Id,
        Name = schedule.Name,
        PlaylistId = schedule.PlaylistId,
        PlaylistName = schedule.Playlist?.Name ?? string.Empty,
        ScreenId = schedule.ScreenId,
        ScreenName = schedule.Screen?.Name,
        GroupId = schedule.GroupId,
        GroupName = schedule.Group?.Name,
        DaysOfWeek = schedule.DaysOfWeek,
        StartTime = schedule.StartTime,
        DurationMinutes = schedule.DurationMinutes,
        Enabled = schedule.Enabled,
        LastTriggeredAt = schedule.LastTriggeredAt,
        TimeZone = timeZone.Id,
        NextRunAt = schedule.Enabled
            ? ScheduleTime.NextRunUtc(schedule.DaysOfWeek, schedule.StartTime, timeZone, nowUtc)
            : null,
        CreatedAt = schedule.CreatedAt
    };
}
