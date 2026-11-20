using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Hubs;
using Pcp.Server.Models;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// Controlador API REST para consulta y administración remota de pantallas.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Viewer")]
[Route("api/screens")]
public class ScreensController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ScreenCommandService _commands;
    private readonly ILogger<ScreensController> _logger;

    public ScreensController(
        AppDbContext db,
        ScreenCommandService commands,
        ILogger<ScreensController> logger)
    {
        _db = db;
        _commands = commands;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/screens
    /// Lista el estado general y telemetría de todas las pantallas registradas.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ScreenInfo>>> GetAll()
    {
        var screens = await _db.Screens
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new ScreenInfo
            {
                Id = s.Id,
                Name = s.Name,
                GroupId = s.GroupId,
                GroupName = s.Group != null ? s.Group.Name : null,
                Status = s.Status,
                LastHeartbeat = s.LastHeartbeat,
                CurrentMedia = s.CurrentMedia,
                Volume = s.Volume,
                CpuPercent = s.CpuPercent,
                RamPercent = s.RamPercent,
                FreeDiskMb = s.FreeDiskMb
            })
            .ToListAsync();

        // Enriquecer en memoria con la miniatura más reciente
        foreach (var sc in screens)
        {
            sc.ThumbnailBase64 = ScreenHub.GetLatestThumbnail(sc.Id);
            sc.RemoteControlAvailable = ScreenHub.GetRemoteControlAvailable(sc.Id);
        }

        return Ok(screens);
    }

    /// <summary>
    /// GET /api/screens/{id}
    /// Obtiene el detalle de una pantalla específica.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ScreenInfo>> GetById(string id)
    {
        var s = await _db.Screens
            .AsNoTracking()
            .Include(x => x.Group)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (s == null)
        {
            return NotFound(new { error = $"Pantalla '{id}' no encontrada." });
        }

        return Ok(new ScreenInfo
        {
            Id = s.Id,
            Name = s.Name,
            GroupId = s.GroupId,
            GroupName = s.Group?.Name,
            Status = s.Status,
            LastHeartbeat = s.LastHeartbeat,
            CurrentMedia = s.CurrentMedia,
            Volume = s.Volume,
            CpuPercent = s.CpuPercent,
            RamPercent = s.RamPercent,
            FreeDiskMb = s.FreeDiskMb,
            ThumbnailBase64 = ScreenHub.GetLatestThumbnail(s.Id),
            RemoteControlAvailable = ScreenHub.GetRemoteControlAvailable(s.Id)
        });
    }

    /// <summary>
    /// POST /api/screens/register
    /// Registra una nueva pantalla o actualiza el nombre de una existente.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ScreenId))
        {
            return BadRequest(new { error = "El parámetro ScreenId es obligatorio." });
        }

        var existing = await _db.Screens.FindAsync(dto.ScreenId);
        bool isNew = false;

        if (existing == null)
        {
            existing = new Screen
            {
                Id = dto.ScreenId,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.ScreenId : dto.Name,
                Token = string.IsNullOrWhiteSpace(dto.Token) ? "dev-token" : dto.Token,
                Status = "offline",
                CreatedAt = DateTime.UtcNow
            };
            _db.Screens.Add(existing);
            isNew = true;
        }
        else
        {
            // El agente debe demostrar que conoce el token de la pantalla antes de
            // modificar su nombre. Sin esta comprobación cualquiera podría renombrarla.
            var provided = string.IsNullOrWhiteSpace(dto.Token) ? "dev-token" : dto.Token;
            if (!string.Equals(existing.Token, provided, StringComparison.Ordinal))
            {
                _logger.LogWarning("Registro rechazado para '{ScreenId}': token incorrecto.", dto.ScreenId);
                return Unauthorized(new { error = "Token de pantalla inválido." });
            }

            if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                existing.Name = dto.Name;
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Registro de pantalla procesado: Id='{ScreenId}', IsNew={IsNew}", dto.ScreenId, isNew);

        return Ok(new
        {
            screenId = existing.Id,
            token = existing.Token,
            isNew
        });
    }

    /// <summary>
    /// POST /api/screens/{id}/command
    /// Envía un comando a la pantalla especificada a través de SignalR.
    /// </summary>
    [HttpPost("{id}/command")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> SendCommand(string id, [FromBody] CommandRequest request)
    {
        var result = await _commands.SendAsync(
            id,
            request.Type,
            request.Payload,
            HttpContext.RequestAborted);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                "not-found" => NotFound(new { error = result.Error }),
                "send-failed" => StatusCode(500, new { error = result.Error }),
                _ => BadRequest(new { error = result.Error })
            };
        }

        return Ok(new
        {
            commandId = result.CommandId,
            status = "sent",
            screenId = id,
            type = request.Type
        });
    }
}

/// <summary>
/// Modelo de solicitud para la emisión de comandos.
/// </summary>
public class CommandRequest
{
    /// <summary>Tipo de instrucción ("play", "pause", "stop", "volume", "shutdown", etc.).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Carga de parámetros específica del comando.</summary>
    public object? Payload { get; set; }
}
