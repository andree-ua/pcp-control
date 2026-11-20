using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    private readonly WakeOnLanService _wake;
    private readonly SyncPlayService _syncPlay;
    private readonly ILogger<ScreensController> _logger;

    public ScreensController(
        AppDbContext db,
        ScreenCommandService commands,
        WakeOnLanService wake,
        SyncPlayService syncPlay,
        ILogger<ScreensController> logger)
    {
        _db = db;
        _commands = commands;
        _wake = wake;
        _syncPlay = syncPlay;
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
                FreeDiskMb = s.FreeDiskMb,
                MacAddress = s.MacAddress,
                LocalIp = s.LocalIp,
                LastConnectionAt = s.LastConnectionAt,
                WolEnabled = s.WolEnabled,
                IsWired = s.IsWired,
                ClockOffsetMs = s.ClockOffsetMs,
                ClockSynced = s.ClockOffsetMs != null
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
            MacAddress = s.MacAddress,
            LocalIp = s.LocalIp,
            LastConnectionAt = s.LastConnectionAt,
            WolEnabled = s.WolEnabled,
            IsWired = s.IsWired,
            ClockOffsetMs = s.ClockOffsetMs,
            ClockSynced = s.ClockOffsetMs != null,
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

        // Datos de red para Wake-on-LAN. Los agentes antiguos no los envían.
        var macAddress = NetworkData.NormalizeMac(dto.MacAddress);
        var (localIp, subnetMask, networkCidr) = NetworkData.NormalizeNetwork(
            dto.LocalIp, dto.SubnetMask, dto.NetworkCidr);

        if (existing == null)
        {
            existing = new Screen
            {
                Id = dto.ScreenId,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.ScreenId : dto.Name,
                Token = string.IsNullOrWhiteSpace(dto.Token) ? "dev-token" : dto.Token,
                Status = "offline",
                MacAddress = macAddress,
                LocalIp = localIp,
                SubnetMask = subnetMask,
                NetworkCidr = networkCidr,
                IsWired = dto.IsWired,
                LastConnectionAt = DateTime.UtcNow,
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

            // Nunca se borra lo ya conocido: si un agente antiguo no reporta red,
            // se conservan la MAC y la IP del último agente que sí lo hizo.
            if (macAddress != null)
            {
                existing.MacAddress = macAddress;
            }

            if (localIp != null)
            {
                existing.LocalIp = localIp;
                existing.SubnetMask = subnetMask;
                existing.NetworkCidr = networkCidr;
            }

            if (dto.IsWired.HasValue)
            {
                existing.IsWired = dto.IsWired.Value;
            }

            existing.LastConnectionAt = DateTime.UtcNow;
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

    /// <summary>
    /// POST /api/screens/{id}/wake
    /// Enciende una pantalla apagada enviando un Magic Packet (Wake-on-LAN).
    /// El paquete lo emite otro agente online de la misma red (relay).
    /// </summary>
    [HttpPost("{id}/wake")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Wake(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WakeRequestDto? request,
        CancellationToken cancellationToken)
    {
        var result = await _wake.WakeAsync(id, request?.RelayScreenId, cancellationToken);

        if (!result.Success)
        {
            if (result.ErrorCode == "cooldown")
            {
                Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            }

            return result.ErrorCode switch
            {
                "not-found" => NotFound(new { error = result.Error }),
                "already-online" => Conflict(new { error = result.Error }),
                "no-relay" => StatusCode(
                    StatusCodes.Status503ServiceUnavailable, new { error = result.Error }),
                "send-failed" => StatusCode(
                    StatusCodes.Status500InternalServerError, new { error = result.Error }),
                "cooldown" => StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { error = result.Error, retryAfterSeconds = result.RetryAfterSeconds }),
                _ => BadRequest(new { error = result.Error })
            };
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// POST /api/screens/sync/play
    /// Programa la reproducción del mismo medio a la vez en varias pantallas
    /// (o en todas las de un grupo). El instante de arranque se fija en el reloj
    /// del servidor; cada agente lo traduce a su reloj local con el offset de /api/time.
    /// </summary>
    [HttpPost("sync/play")]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> SyncPlay(
        [FromBody] SyncPlayRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _syncPlay.DispatchAsync(request, cancellationToken);

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
