using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Hubs;

/// <summary>
/// Hub central de SignalR para la comunicación bidireccional entre el servidor,
/// los agentes instalados en las mini-PCs y los paneles de control web.
/// </summary>
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
    /// Se invoca al establecerse una nueva conexión.
    /// Si provienen parámetros screenId y token, se trata de un agente; si no, se une al grupo "panel".
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var token = httpContext?.Request.Query["token"].ToString();
        var screenId = httpContext?.Request.Query["screenId"].ToString();

        if (!string.IsNullOrEmpty(screenId))
        {
            // Verificamos o recuperamos la pantalla en la base de datos
            var screen = await _db.Screens.FirstOrDefaultAsync(s => s.Id == screenId);

            if (screen == null)
            {
                // Registro automático en fase de desarrollo si la pantalla aún no estaba en BD
                screen = new Models.Screen
                {
                    Id = screenId,
                    Name = $"Pantalla {screenId}",
                    Token = string.IsNullOrEmpty(token) ? "dev-token" : token,
                    Status = "online",
                    LastHeartbeat = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Screens.Add(screen);
            }
            else
            {
                // Validación de token (en Fase 0 comprobamos coincidencia básica o token dev)
                if (!string.IsNullOrEmpty(token) && screen.Token != token && token != "dev-token")
                {
                    _logger.LogWarning("Conexión rechazada: Token incorrecto para screenId '{ScreenId}'", screenId);
                    Context.Abort();
                    return;
                }

                screen.Status = "online";
                screen.LastHeartbeat = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            // Asociar identificador de conexión
            _connectionManager.RegisterConnection(screenId, Context.ConnectionId);

            _logger.LogInformation("Agente conectado: ScreenId='{ScreenId}', ConnectionId='{ConnectionId}'", screenId, Context.ConnectionId);

            // Informar a clientes del panel administrativo
            await Clients.Group("panel").SendAsync("ScreenStatusChanged", screenId, "online");
        }
        else
        {
            // Conexión perteneciente al panel web o monitor
            await Groups.AddToGroupAsync(Context.ConnectionId, "panel");
            _logger.LogInformation("Cliente de panel web conectado: ConnectionId='{ConnectionId}'", Context.ConnectionId);
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
    public async Task Heartbeat(HeartbeatDto dto)
    {
        if (string.IsNullOrEmpty(dto.ScreenId)) return;

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
            ThumbnailBase64 = GetLatestThumbnail(screen.Id),
            RemoteControlAvailable = dto.RemoteControlAvailable
        };

        await Clients.Group("panel").SendAsync("HeartbeatReceived", info);
        _logger.LogDebug("Heartbeat procesado de ScreenId='{ScreenId}'. CPU={Cpu}%, RAM={Ram}%", dto.ScreenId, dto.CpuPercent, dto.RamPercent);
    }

    /// <summary>
    /// Recibe la confirmación o error de ejecución de un comando por parte del agente.
    /// </summary>
    public async Task CommandResult(CommandResultDto dto)
    {
        var command = await _db.Commands.FindAsync(dto.CommandId);
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
    public Task FileResult(FileResultDto result)
    {
        _fileRequests.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recibe el estado del túnel VNC abierto por un agente (lo consume la petición REST en curso).
    /// </summary>
    public Task VncTunnelStatus(VncTunnelStatusDto dto)
    {
        _vncSessions.ReportStatus(dto);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recibe un fotograma/miniatura periódica enviada por el agente y la retransmite en vivo al panel.
    /// </summary>
    public async Task SendThumbnail(ThumbnailDto dto)
    {
        if (string.IsNullOrEmpty(dto.ScreenId) || string.IsNullOrEmpty(dto.ThumbnailBase64)) return;

        // Guardar en memoria para consulta rápida inmediata del dashboard
        _latestThumbnails[dto.ScreenId] = dto.ThumbnailBase64;
        _logger.LogInformation("Thumbnail recibido de {ScreenId} ({Length} bytes Base64)", dto.ScreenId, dto.ThumbnailBase64.Length);

        // Reenviar a los observadores del panel web en tiempo real
        await Clients.Group("panel").SendAsync("ThumbnailReceived", dto);
    }
}
