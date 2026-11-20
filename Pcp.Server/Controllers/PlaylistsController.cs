using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// CRUD de playlists y reproducción inmediata (Bloque 4).
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Viewer")]
[Route("api/playlists")]
public class PlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PlaylistPlaybackService _playback;
    private readonly ILogger<PlaylistsController> _logger;

    public PlaylistsController(
        AppDbContext db,
        PlaylistPlaybackService playback,
        ILogger<PlaylistsController> logger)
    {
        _db = db;
        _playback = playback;
        _logger = logger;
    }

    /// <summary>GET /api/playlists — lista todas las playlists con sus elementos ordenados.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlaylistDto>>> GetAll(CancellationToken cancellationToken)
    {
        var playlists = await _db.Playlists
            .AsNoTracking()
            .Include(playlist => playlist.Items)
            .OrderBy(playlist => playlist.Name)
            .ToListAsync(cancellationToken);

        return Ok(playlists.Select(ToDto));
    }

    /// <summary>GET /api/playlists/{id}</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlaylistDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var playlist = await _db.Playlists
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{id}' no encontrada." });
        }

        return Ok(ToDto(playlist));
    }

    /// <summary>POST /api/playlists — crea una playlist con sus elementos.</summary>
    [HttpPost]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Create(
        [FromBody] PlaylistUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var name = ValidateName(dto.Name);
        if (name == null)
        {
            return BadRequest(new { error = "El nombre es obligatorio y no puede superar 100 caracteres." });
        }

        if (!TryBuildItems(dto.Items, out var items, out var error))
        {
            return BadRequest(new { error });
        }

        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = name,
            Loop = dto.Loop,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = items
        };

        foreach (var item in items)
        {
            item.PlaylistId = playlist.Id;
        }

        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Playlist creada: '{Name}' ({PlaylistId}) con {Count} elemento(s).",
            playlist.Name, playlist.Id, items.Count);

        return Ok(ToDto(playlist));
    }

    /// <summary>PUT /api/playlists/{id} — renombra y reemplaza los elementos.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] PlaylistUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var name = ValidateName(dto.Name);
        if (name == null)
        {
            return BadRequest(new { error = "El nombre es obligatorio y no puede superar 100 caracteres." });
        }

        if (!TryBuildItems(dto.Items, out var items, out var error))
        {
            return BadRequest(new { error });
        }

        var playlist = await _db.Playlists
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{id}' no encontrada." });
        }

        playlist.Name = name;
        playlist.Loop = dto.Loop;
        playlist.UpdatedAt = DateTime.UtcNow;

        // Reemplazo por diferencias: se reutilizan las filas existentes por
        // posición y solo se borran/añaden las que sobran o faltan. Evita
        // borrar y recrear todas las filas en el mismo SaveChanges.
        var existingItems = playlist.Items.OrderBy(item => item.Position).ToList();

        for (var index = 0; index < items.Count; index++)
        {
            var incoming = items[index];

            if (index < existingItems.Count)
            {
                var current = existingItems[index];
                current.Position = incoming.Position;
                current.MediaPath = incoming.MediaPath;
                current.DurationSeconds = incoming.DurationSeconds;
                current.Volume = incoming.Volume;
            }
            else
            {
                // Un Guid preasignado haría que EF lo tratara como una fila
                // existente y emitiera un UPDATE. Marcarlo Added es explícito y
                // el FK del padre ya está fijado.
                incoming.PlaylistId = playlist.Id;
                playlist.Items.Add(incoming);
                _db.PlaylistItems.Add(incoming);
            }
        }

        for (var index = items.Count; index < existingItems.Count; index++)
        {
            playlist.Items.Remove(existingItems[index]);
            _db.PlaylistItems.Remove(existingItems[index]);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Playlist actualizada: '{Name}' ({PlaylistId}) con {Count} elemento(s).",
            playlist.Name, playlist.Id, items.Count);

        return Ok(ToDto(playlist));
    }

    /// <summary>DELETE /api/playlists/{id} — elimina la playlist, sus elementos y sus horarios.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var playlist = await _db.Playlists.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{id}' no encontrada." });
        }

        _db.Playlists.Remove(playlist);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Playlist eliminada: '{Name}' ({PlaylistId}).", playlist.Name, playlist.Id);

        return NoContent();
    }

    /// <summary>
    /// POST /api/playlists/{id}/play — reproduce la playlist ahora en una pantalla
    /// o en un grupo. El instante de arranque se fija en el reloj del servidor.
    /// </summary>
    [HttpPost("{id:guid}/play")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Play(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PlaylistPlayRequestDto? request,
        CancellationToken cancellationToken)
    {
        var playlist = await _db.Playlists
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{id}' no encontrada." });
        }

        request ??= new PlaylistPlayRequestDto();

        var leadSeconds = Math.Clamp(
            request.StartInSeconds,
            PlaylistPlaybackService.MinLeadSeconds,
            PlaylistPlaybackService.MaxLeadSeconds);

        var result = await _playback.DispatchAsync(
            playlist,
            request.ScreenIds,
            request.GroupId,
            DateTime.UtcNow.AddSeconds(leadSeconds),
            cancellationToken);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                "not-found" => NotFound(new { error = result.Error }),
                _ => BadRequest(new { error = result.Error })
            };
        }

        return Ok(result.Response);
    }

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 100)
        {
            return null;
        }

        return trimmed;
    }

    private static bool TryBuildItems(
        IEnumerable<PlaylistItemInputDto>? inputs,
        out List<PlaylistItem> items,
        out string? error)
    {
        items = new List<PlaylistItem>();
        error = null;

        var position = 0;

        foreach (var input in inputs ?? Enumerable.Empty<PlaylistItemInputDto>())
        {
            var mediaPath = input.MediaPath?.Trim();
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                error = $"El elemento #{position + 1} no tiene ruta de medio.";
                return false;
            }

            if (mediaPath.Length > 500)
            {
                error = $"La ruta del elemento #{position + 1} supera 500 caracteres.";
                return false;
            }

            items.Add(new PlaylistItem
            {
                Id = Guid.NewGuid(),
                Position = position++,
                MediaPath = mediaPath,
                // 0 = reproducir hasta el final natural; máximo 24 h por seguridad.
                DurationSeconds = Math.Clamp(input.DurationSeconds, 0, 86_400),
                // 0 = volumen por defecto de la playlist.
                Volume = Math.Clamp(input.Volume, 0, 100)
            });
        }

        return true;
    }

    private static PlaylistDto ToDto(Playlist playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Loop = playlist.Loop,
        CreatedAt = playlist.CreatedAt,
        UpdatedAt = playlist.UpdatedAt,
        Items = playlist.Items
            .OrderBy(item => item.Position)
            .Select(item => new PlaylistItemDto
            {
                Id = item.Id,
                Position = item.Position,
                MediaPath = item.MediaPath,
                DurationSeconds = item.DurationSeconds,
                Volume = item.Volume
            })
            .ToList()
    };
}
