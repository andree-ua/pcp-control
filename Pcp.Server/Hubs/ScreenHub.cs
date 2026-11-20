using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Hubs;

/// <summary>
/// Hub central de SignalR para la comunicación bidireccional entre el servidor,
/// los agentes instalados en las mini-PCs y los paneles de control web.
/// La identidad ya no se toma del query string: el JWT identifica agentes y usuarios.
/// </summary>
[Authorize]
public class ScreenHub : Hub
{
    private readonly AppDbContext _db;
    private readonly IScreenConnectionManager _connectionManager;
    private readonly IFileRequestManager _fileRequests;
    private readonly IVncSessionManager _vncSessions;
    private readonly ILogger<ScreenHub> _logger;

    // Almacén en memoria de las últimas miniaturas capturadas por pantalla
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _latestThumbnails = new();

    // Capacidad de control remoto reportada por cada agente (no se persiste en BD)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _remoteControlAvailable = new();

    public static string? GetLatestThumbnail(string screenId)
    {
        _latestThumbnails.TryGetValue(screenId, out var thumb);
        return thumb;
    }

    /// <summary>
    /// Indica si la pantalla reportó tener un servidor VNC disponible.
    /// </summary>
    public static bool GetRemoteControlAvailable(string screenId)
        => _remoteControlAvailable.TryGetValue(screenId, out var available) && available;

    public ScreenHub(
        AppDbContext db,
        IScreenConnectionManager connectionManager,
        IFileRequestManager fileRequests,
        IVncSessionManager vncSessions,
        ILogger<ScreenHub> logger)
    {
        _db = db;
        _connectionManager = connectionManager;
        _fileRequests = fileRequests;
        _vncSessions = vncSessions;
        _logger = logger;
    }

    /// <summary>
    /// Se invoca al establecerse una nueva conexión. Los agentes se identifican por el
    /// claim screen_id de su JWT; los usuarios del panel entran al grupo "panel".
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var role = Context.User?.FindFirst("role")?.Value;

        if (string.Equals(role, TokenService.AgentRole, StringComparison.Ordinal))
        {
            var screenId = Context.User?.FindFirst(TokenService.ScreenIdClaim)?.Value;
            if (string.IsNullOrWhiteSpace(screenId))
            {
                _logger.LogWarning("Conexión de agente rechazada: el token no incluye screen_id.");
                Context.Abort();
                return;
            }

            var screen = await _db.Screens.FirstOrDefaultAsync(s => s.Id == screenId);
            if (screen == null)
            {
                _logger.LogWarning("Conexión de agente rechazada: pantalla '{ScreenId}' no registrada.", screenId);
                Context.Abort();
                return;
            }

            screen.Status = "online";
            screen.LastHeartbeat = DateTime.UtcNow;
            screen.LastConnectionAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _connectionManager.RegisterConnection(screenId, Context.ConnectionId);

            _logger.LogInformation(
                "Agente conectado: ScreenId='{ScreenId}', ConnectionId='{ConnectionId}'",
                screenId, Context.ConnectionId);

            await Clients.Group("panel").SendAsync("ScreenStatusChanged", screenId, "online");
        }
        else if (string.Equals(role, TokenService.AdminRole, StringComparison.Ordinal) ||
                 string.Equals(role, TokenService.ViewerRole, StringComparison.Ordinal))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "panel");
            _logger.LogInformation(
                "Cliente de panel web conectado: ConnectionId='{ConnectionId}', Rol='{Role}'",
                Context.ConnectionId, role);
        }
        else
        {
            _logger.LogWarning("Conexión rechazada: rol no reconocido ('{Role}').", role);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Se ejecuta cuando un cliente cierra la conexión o sufre una desconexión no esperada.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var screenId = _connectionManager.GetScreenId(Context.ConnectionId);
        if (!string.IsNullOrEmpty(screenId))
        {
            _connectionManager.RemoveConnection(Context.ConnectionId);
            _fileRequests.CancelPending(screenId, "El agente se desconectó durante la operación.");

            // Cerrar cualquier sesión de escritorio remoto activa de esa pantalla
            _vncSessions.CloseByScreen(screenId, "El agente se desconectó.");
            _remoteControlAvailable[screenId] = false;

            var screen = await _db.Screens.FindAsync(screenId);
            if (screen != null)
            {
                screen.Status = "offline";
                await _db.SaveChangesAsync();
                _logger.LogInformation("Agente desconectado: ScreenId='{ScreenId}'", screenId);

                // Notificar al panel sobre la desconexión
                await Clients.Group("panel").SendAsync("ScreenStatusChanged", screenId, "offline");
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Recibe el latido periódico (telemetría) enviado por un agente.
    /// </summary>
    [Authorize(Roles = TokenService.AgentRole)]
    public async Task Heartbeat(HeartbeatDto dto)
    {
        if (string.IsNullOrEmpty(dto.ScreenId)) return;

        if (!IsOwnScreen(dto.ScreenId))
        {
            _logger.LogWarning("Heartbeat rechazado: el agente de '{Claim}' intentó reportar '{ScreenId}'.",
                CurrentAgentScreenId, dto.ScreenId);
            return;
        }

        var screen = await _db.Screens
            .Include(s => s.Group)
            .FirstOrDefaultAsync(s => s.Id == dto.ScreenId);
        if (screen == null)
        {
            _logger.LogWarning("Heartbeat recibido para pantalla no registrada: '{ScreenId}'", dto.ScreenId);
            return;
        }

        // Actualizar métricas de la pantalla
        screen.Status = "online";
        screen.LastHeartbeat = dto.Timestamp == default ? DateTime.UtcNow : dto.Timestamp;
        screen.CurrentMedia = dto.CurrentMedia;
        screen.Volume = dto.Volume;
        screen.CpuPercent = dto.CpuPercent;
        screen.RamPercent = dto.RamPercent;
        screen.FreeDiskMb = dto.FreeDiskMb;

        // Datos de red para Wake-on-LAN. Los agentes antiguos envían null y no
        // deben borrar lo que reportó el agente nuevo la última vez.
        var mac = NetworkData.NormalizeMac(dto.MacAddress);
        if (mac != null)
        {
            screen.MacAddress = mac;
        }

        var (localIp, subnetMask, networkCidr) = NetworkData.NormalizeNetwork(
            dto.LocalIp, dto.SubnetMask, dto.NetworkCidr);

        if (localIp != null)
        {
            screen.LocalIp = localIp;
            screen.SubnetMask = subnetMask;
            screen.NetworkCidr = networkCidr;
        }

        if (dto.IsWired.HasValue)
        {
            screen.IsWired = dto.IsWired.Value;
        }

        await _db.SaveChangesAsync();

        _remoteControlAvailable[screen.Id] = dto.RemoteControlAvailable;

        // Recuperar el mapeo si otra conexión duplicada lo reemplazó y luego cayó.
        if (_connectionManager.GetScreenId(Context.ConnectionId) != screen.Id)
        {
            _connectionManager.RegisterConnection(screen.Id, Context.ConnectionId);
        }

        // Enviar evento de telemetría a los suscriptores del panel
        var info = new ScreenInfo
        {
            Id = screen.Id,
            Name = screen.Name,
            GroupId = screen.GroupId,
            GroupName = screen.Group?.Name,
            Status = screen.Status,
            LastHeartbeat = screen.LastHeartbeat,
            CurrentMedia = screen.CurrentMedia,
            Volume = screen.Volume,
            CpuPercent = screen.CpuPercent,
            RamPercent = screen.RamPercent,
            FreeDiskMb = screen.FreeDiskMb,
            MacAddress = screen.MacAddress,
            LocalIp = screen.LocalIp,
            LastConnectionAt = screen.LastConnectionAt,
            WolEnabled = screen.WolEnabled,
            IsWired = screen.IsWired,
            ThumbnailBase64 = GetLatestThumbnail(screen.Id),
            RemoteControlAvailable = dto.RemoteControlAvailable
        };

        await Clients.Group("panel").SendAsync("HeartbeatReceived", info);
        _logger.LogDebug("Heartbeat procesado de ScreenId='{ScreenId}'. CPU={Cpu}%, RAM={Ram}%", dto.ScreenId, dto.CpuPercent, dto.RamPercent);
    }

    /// <summary>
    /// Recibe la confirmación o error de ejecución de un comando por parte del agente.
    /// </summary>
    [Authorize(Roles = TokenService.AgentRole)]
    public async Task CommandResult(CommandResultDto dto)
    {
        if (!IsOwnScreen(dto.ScreenId))
        {
            _logger.LogWarning("CommandResult rechazado: el agente de '{Claim}' intentó reportar '{ScreenId}'.",
                CurrentAgentScreenId, dto.ScreenId);
            return;
        }

        var command = await _db.Commands.FindAsync(dto.CommandId);
        if (command != null && !string.Equals(command.ScreenId, dto.ScreenId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CommandResult rechazado: el comando {CommandId} pertenece a '{Owner}'.", dto.CommandId, command.ScreenId);
            return;
        }

        if (command != null)
        {
            command.Status = dto.Status;
            command.ExecutedAt = dto.Timestamp == default ? DateTime.UtcNow : dto.Timestamp;
            command.ResultDetail = dto.Detail;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Resultado de comando {CommandId} para {ScreenId}: Status={Status}, Detalle='{Detail}'",
                dto.CommandId, dto.ScreenId, dto.Status, dto.Detail);
        }
        else
        {
            _logger.LogWarning("Resultado para comando no existente en BD: {CommandId}", dto.CommandId);
        }

        // Propagar resultado a los paneles en tiempo real
        await Clients.Group("panel").SendAsync("CommandResultReceived", dto);
    }

    /// <summary>
    /// Recibe el resultado de una operación de archivos ejecutada por el agente.
    /// </summary>
    [Authorize(Roles = TokenService.AgentRole)]
    public Task FileResult(FileResultDto result)
    {
        if (!IsOwnScreen(result.ScreenId))
        {
            _logger.LogWarning("FileResult rechazado: el agente de '{Claim}' intentó responder por '{ScreenId}'.",
                CurrentAgentScreenId, result.ScreenId);
            return Task.CompletedTask;
        }

        _fileRequests.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recibe el estado del túnel VNC abierto por un agente (lo consume la petición REST en curso).
    /// </summary>
    [Authorize(Roles = TokenService.AgentRole)]
    public Task VncTunnelStatus(VncTunnelStatusDto dto)
    {
        if (!IsOwnScreen(dto.ScreenId))
        {
            _logger.LogWarning("VncTunnelStatus rechazado: el agente de '{Claim}' intentó reportar '{ScreenId}'.",
                CurrentAgentScreenId, dto.ScreenId);
            return Task.CompletedTask;
        }

        _vncSessions.ReportStatus(dto);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recibe un fotograma/miniatura periódica enviada por el agente y la retransmite en vivo al panel.
    /// </summary>
    [Authorize(Roles = TokenService.AgentRole)]
    public async Task SendThumbnail(ThumbnailDto dto)
    {
        if (string.IsNullOrEmpty(dto.ScreenId) || string.IsNullOrEmpty(dto.ThumbnailBase64)) return;

        if (!IsOwnScreen(dto.ScreenId))
        {
            _logger.LogWarning("Thumbnail rechazado: el agente de '{Claim}' intentó reportar '{ScreenId}'.",
                CurrentAgentScreenId, dto.ScreenId);
            return;
        }

        // Guardar en memoria para consulta rápida inmediata del dashboard
        _latestThumbnails[dto.ScreenId] = dto.ThumbnailBase64;
        _logger.LogInformation("Thumbnail recibido de {ScreenId} ({Length} bytes Base64)", dto.ScreenId, dto.ThumbnailBase64.Length);

        // Reenviar a los observadores del panel web en tiempo real
        await Clients.Group("panel").SendAsync("ThumbnailReceived", dto);
    }

    private string? CurrentAgentScreenId => Context.User?.FindFirst(TokenService.ScreenIdClaim)?.Value;

    private bool IsOwnScreen(string? screenId)
        => !string.IsNullOrWhiteSpace(screenId) &&
           string.Equals(CurrentAgentScreenId, screenId, StringComparison.OrdinalIgnoreCase);
}
