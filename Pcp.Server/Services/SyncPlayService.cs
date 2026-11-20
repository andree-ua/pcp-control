using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Orquesta una reproducción sincronizada (Bloque 3).
///
/// El servidor no reproduce nada: fija un instante absoluto de arranque en su
/// propio reloj y encarga el comando "sync-play" a cada pantalla. Cada agente
/// convierte ese instante a su reloj local con el offset medido contra /api/time,
/// de modo que el desfase de los relojes del stick no afecta a la sincronía.
/// </summary>
public class SyncPlayService
{
    private const int MinLeadSeconds = 3;
    private const int MaxLeadSeconds = 120;

    private readonly AppDbContext _db;
    private readonly ScreenCommandService _commands;
    private readonly ILogger<SyncPlayService> _logger;

    public SyncPlayService(
        AppDbContext db,
        ScreenCommandService commands,
        ILogger<SyncPlayService> logger)
    {
        _db = db;
        _commands = commands;
        _logger = logger;
    }

    public async Task<SyncPlayDispatchResult> DispatchAsync(
        SyncPlayRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var mediaPath = request.MediaPath?.Trim();
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return Fail("invalid", "El campo 'mediaPath' es obligatorio.");
        }

        var screens = await ResolveTargetsAsync(request, cancellationToken);
        if (screens == null)
        {
            return Fail("not-found", request.GroupId.HasValue
                ? $"Grupo '{request.GroupId}' no encontrado."
                : "Ninguna de las pantallas indicadas existe.");
        }

        if (screens.Count == 0)
        {
            return Fail("invalid", request.GroupId.HasValue
                ? "El grupo no tiene pantallas asignadas."
                : "Debes indicar al menos una pantalla o un grupo.");
        }

        var leadSeconds = Math.Clamp(request.StartInSeconds, MinLeadSeconds, MaxLeadSeconds);
        var startAt = DateTimeOffset.UtcNow.AddSeconds(leadSeconds);
        var startAtUnixMs = startAt.ToUnixTimeMilliseconds();
        var syncToken = Guid.NewGuid().ToString("N")[..12];
        var volume = Math.Clamp(request.Volume, 0, 100);

        var response = new SyncPlayResponseDto
        {
            SyncToken = syncToken,
            StartAtUnixMs = startAtUnixMs,
            StartAtUtc = startAt.UtcDateTime,
            StartInSeconds = leadSeconds,
            Total = screens.Count
        };

        foreach (var screen in screens)
        {
            // El mismo StartAtUnixMs para todas: la sincronía no depende del
            // orden ni de la latencia de cada envío.
            var payload = new SyncPlayPayload
            {
                MediaPath = mediaPath,
                Loop = request.Loop,
                Volume = volume,
                FitMode = string.IsNullOrWhiteSpace(request.FitMode) ? "cover" : request.FitMode,
                StartAtUnixMs = startAtUnixMs,
                SyncToken = syncToken
            };

            var dispatch = await _commands.SendAsync(screen.Id, "sync-play", payload, cancellationToken);

            if (dispatch.Success)
            {
                response.Sent++;
            }
            else if (dispatch.ErrorCode == "not-connected")
            {
                response.Offline++;
            }
            else
            {
                response.Failed++;
            }

            response.Results.Add(new SyncPlayResultItemDto
            {
                ScreenId = screen.Id,
                Success = dispatch.Success,
                CommandId = dispatch.CommandId,
                Error = dispatch.Error
            });
        }

        _logger.LogInformation(
            "Reproducción sincronizada '{Media}' programada para {StartAt:O} (token {Token}): " +
            "{Sent} enviadas, {Offline} offline, {Failed} fallidas",
            mediaPath, startAt.UtcDateTime, syncToken, response.Sent, response.Offline, response.Failed);

        return new SyncPlayDispatchResult { Success = true, Response = response };
    }

    /// <summary>
    /// Resuelve las pantallas destino. El grupo tiene prioridad sobre la lista
    /// explícita para que el panel no tenga que mantener ambas cosas en sincronía.
    /// Devuelve null si el destino indicado no existe.
    /// </summary>
    private async Task<List<Screen>?> ResolveTargetsAsync(
        SyncPlayRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.GroupId.HasValue)
        {
            var groupExists = await _db.ScreenGroups
                .AsNoTracking()
                .AnyAsync(g => g.Id == request.GroupId.Value, cancellationToken);

            if (!groupExists)
            {
                return null;
            }

            return await _db.Screens
                .AsNoTracking()
                .Where(s => s.GroupId == request.GroupId.Value)
                .OrderBy(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        var ids = (request.ScreenIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new List<Screen>();
        }

        var found = await _db.Screens
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);

        return found.Count == 0 ? null : found;
    }

    private static SyncPlayDispatchResult Fail(string errorCode, string error) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Error = error
    };
}

/// <summary>Resultado del despacho de una reproducción sincronizada.</summary>
public sealed class SyncPlayDispatchResult
{
    public bool Success { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Error { get; init; }

    public SyncPlayResponseDto? Response { get; init; }
}
