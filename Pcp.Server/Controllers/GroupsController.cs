using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// Gestión de grupos de pantallas y envío de comandos masivos.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Viewer")]
[Route("api/groups")]
public class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ScreenCommandService _commands;
    private readonly ILogger<GroupsController> _logger;

    public GroupsController(
        AppDbContext db,
        ScreenCommandService commands,
        ILogger<GroupsController> logger)
    {
        _db = db;
        _commands = commands;
        _logger = logger;
    }

    /// <summary>GET /api/groups — lista grupos con sus pantallas asignadas.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<GroupInfoDto>>> GetAll(CancellationToken cancellationToken)
    {
        var groups = await _db.ScreenGroups
            .AsNoTracking()
            .Include(g => g.Screens)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return Ok(groups.Select(ToDto));
    }

    /// <summary>POST /api/groups — crea un grupo y asigna las pantallas indicadas.</summary>
    [HttpPost]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Create(
        [FromBody] GroupUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var name = ValidateName(dto.Name);
        if (name == null)
        {
            return BadRequest(new { error = "El nombre del grupo es obligatorio y no puede superar 100 caracteres." });
        }

        var group = new ScreenGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        _db.ScreenGroups.Add(group);

        var selected = await GetSelectedScreensAsync(dto.ScreenIds, cancellationToken);
        foreach (var screen in selected)
        {
            screen.GroupId = group.Id;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Grupo creado: '{Name}' ({GroupId}) con {Count} pantalla(s)",
            group.Name, group.Id, selected.Count);

        return Ok(ToDto(group, selected.Select(s => s.Id)));
    }

    /// <summary>PUT /api/groups/{id} — renombra el grupo y reemplaza su membresía.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] GroupUpsertDto dto,
        CancellationToken cancellationToken)
    {
        var name = ValidateName(dto.Name);
        if (name == null)
        {
            return BadRequest(new { error = "El nombre del grupo es obligatorio y no puede superar 100 caracteres." });
        }

        var group = await _db.ScreenGroups
            .Include(g => g.Screens)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (group == null)
        {
            return NotFound(new { error = $"Grupo '{id}' no encontrado." });
        }

        group.Name = name;

        var selected = await GetSelectedScreensAsync(dto.ScreenIds, cancellationToken);
        var selectedIds = new HashSet<string>(selected.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var screen in group.Screens.ToList())
        {
            if (!selectedIds.Contains(screen.Id))
            {
                screen.GroupId = null;
            }
        }

        foreach (var screen in selected)
        {
            screen.GroupId = group.Id;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Grupo actualizado: '{Name}' ({GroupId}) con {Count} pantalla(s)",
            group.Name, group.Id, selected.Count);

        return Ok(ToDto(group, selectedIds));
    }

    /// <summary>DELETE /api/groups/{id} — elimina el grupo y desasigna sus pantallas.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var group = await _db.ScreenGroups
            .Include(g => g.Screens)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (group == null)
        {
            return NotFound(new { error = $"Grupo '{id}' no encontrado." });
        }

        foreach (var screen in group.Screens)
        {
            screen.GroupId = null;
        }

        _db.ScreenGroups.Remove(group);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Grupo eliminado: '{Name}' ({GroupId})", group.Name, group.Id);

        return NoContent();
    }

    /// <summary>
    /// POST /api/groups/{id}/command — envía un comando a todas las pantallas del grupo.
    /// Las pantallas offline se omiten y se informan en el resumen.
    /// </summary>
    [HttpPost("{id:guid}/command")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> SendCommand(
        Guid id,
        [FromBody] CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            return BadRequest(new { error = "El campo 'type' del comando es obligatorio." });
        }

        var groupExists = await _db.ScreenGroups
            .AsNoTracking()
            .AnyAsync(g => g.Id == id, cancellationToken);

        if (!groupExists)
        {
            return NotFound(new { error = $"Grupo '{id}' no encontrado." });
        }

        var screens = await _db.Screens
            .AsNoTracking()
            .Where(s => s.GroupId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);

        if (screens.Count == 0)
        {
            return BadRequest(new { error = "El grupo no tiene pantallas asignadas." });
        }

        var response = new GroupCommandResponseDto
        {
            GroupId = id,
            Total = screens.Count
        };

        foreach (var screen in screens)
        {
            var result = await _commands.SendAsync(
                screen.Id,
                request.Type,
                request.Payload,
                cancellationToken);

            if (result.Success)
            {
                response.Sent++;
            }
            else if (result.ErrorCode == "not-connected")
            {
                response.Offline++;
            }
            else
            {
                response.Failed++;
            }

            response.Results.Add(new GroupCommandResultItemDto
            {
                ScreenId = screen.Id,
                Success = result.Success,
                CommandId = result.CommandId,
                Error = result.Error
            });
        }

        _logger.LogInformation(
            "Comando masivo '{Type}' para grupo {GroupId}: {Sent} enviados, {Offline} offline, {Failed} fallidos",
            request.Type, id, response.Sent, response.Offline, response.Failed);

        return Ok(response);
    }

    private async Task<List<Screen>> GetSelectedScreensAsync(
        IEnumerable<string>? screenIds,
        CancellationToken cancellationToken)
    {
        if (screenIds == null)
        {
            return new List<Screen>();
        }

        var ids = screenIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new List<Screen>();
        }

        return await _db.Screens
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken);
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

    private static GroupInfoDto ToDto(ScreenGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        CreatedAt = group.CreatedAt,
        ScreenIds = group.Screens
            .Select(s => s.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList()
    };

    private static GroupInfoDto ToDto(ScreenGroup group, IEnumerable<string> screenIds) => new()
    {
        Id = group.Id,
        Name = group.Name,
        CreatedAt = group.CreatedAt,
        ScreenIds = screenIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList()
    };
}
