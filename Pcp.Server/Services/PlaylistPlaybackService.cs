using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Lanza una playlist en una o varias pantallas (Bloque 4).
///
/// El servidor no reproduce nada: envía el comando <c>play-playlist</c> con la
/// lista completa y un instante de arranque absoluto. Cada agente avanza los
/// elementos por su cuenta, así que el servidor no tiene que cronometrar nada.
/// Lo usan el endpoint manual del panel y el programador de horarios.
/// </summary>
public class PlaylistPlaybackService
{
    public const int MinLeadSeconds = 3;
    public const int MaxLeadSeconds = 120;
    public const int DefaultLeadSeconds = 8;

    private readonly AppDbContext _db;
    private readonly ScreenCommandService _commands;
    private readonly ILogger<PlaylistPlaybackService> _logger;

    public PlaylistPlaybackService(
        AppDbContext db,
        ScreenCommandService commands,
        ILogger<PlaylistPlaybackService> logger)
    {
        _db = db;
        _commands = commands;
        _logger = logger;
    }

    /// <summary>
    /// Envía la playlist al destino indicado. El grupo tiene prioridad sobre la
    /// lista explícita de pantallas, igual que en los comandos masivos.
    /// </summary>
    public async Task<PlaylistDispatchResult> DispatchAsync(
        Playlist playlist,
        IReadOnlyCollection<string>? screenIds,
        Guid? groupId,
        DateTime startAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (playlist.Items.Count == 0)
        {
            return Fail("invalid", $"La playlist '{playlist.Name}' no tiene elementos.");
        }

        var targets = await ResolveTargetsAsync(screenIds, groupId, cancellationToken);
        if (targets == null)
        {
            return Fail("not-found", groupId.HasValue
                ? $"Grupo '{groupId}' no encontrado."
                : "Ninguna de las pantallas indicadas existe.");
        }

        if (targets.Count == 0)
        {
            return Fail("invalid", groupId.HasValue
                ? "El grupo no tiene pantallas asignadas."
                : "Debes indicar al menos una pantalla o un grupo.");
        }

        var items = playlist.Items
            .OrderBy(item => item.Position)
            .Select(item => new PlaylistItemDto
            {
                Id = item.Id,
                Position = item.Position,
                MediaPath = item.MediaPath,
                DurationSeconds = item.DurationSeconds,
                Volume = item.Volume
            })
            .ToList();

        var payload = new PlaylistCommandPayload
        {
            PlaylistId = playlist.Id,
            Name = playlist.Name,
            Loop = playlist.Loop,
            StartAtUnixMs = new DateTimeOffset(startAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Items = items
        };

        var response = new PlaylistPlayResponseDto
        {
            PlaylistId = playlist.Id,
            Name = playlist.Name,
            StartAtUnixMs = payload.StartAtUnixMs!.Value,
            StartAtUtc = startAtUtc,
            Total = targets.Count
        };

        foreach (var screenId in targets)
        {
            var dispatch = await _commands.SendAsync(screenId, "play-playlist", payload, cancellationToken);

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

            response.Results.Add(new PlaylistDispatchItemDto
            {
                ScreenId = screenId,
                Success = dispatch.Success,
                CommandId = dispatch.CommandId,
                Error = dispatch.Error
            });
        }

        _logger.LogInformation(
            "Playlist '{Playlist}' ({Items} elementos) programada para {StartAt:O}: " +
            "{Sent} enviadas, {Offline} offline, {Failed} fallidas",
            playlist.Name, items.Count, startAtUtc, response.Sent, response.Offline, response.Failed);

        return new PlaylistDispatchResult { Success = true, Response = response };
    }

    private async Task<List<string>?> ResolveTargetsAsync(
        IReadOnlyCollection<string>? screenIds,
        Guid? groupId,
        CancellationToken cancellationToken)
    {
        if (groupId.HasValue)
        {
            var groupExists = await _db.ScreenGroups
                .AsNoTracking()
                .AnyAsync(g => g.Id == groupId.Value, cancellationToken);

            if (!groupExists)
            {
                return null;
            }

            return await _db.Screens
                .AsNoTracking()
                .Where(s => s.GroupId == groupId.Value)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        var ids = (screenIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new List<string>();
        }

        var found = await _db.Screens
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        return found.Count == 0 ? null : found;
    }

    private static PlaylistDispatchResult Fail(string errorCode, string error) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Error = error
    };
}

/// <summary>Resultado del lanzamiento de una playlist.</summary>
public sealed class PlaylistDispatchResult
{
    public bool Success { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Error { get; init; }

    public PlaylistPlayResponseDto? Response { get; init; }
}
