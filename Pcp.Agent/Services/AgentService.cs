using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pcp.Agent.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Servicio principal en segundo plano del Agente PCP.
/// Gestiona la conexión persistente por SignalR con el servidor central,
/// la telemetría periódica (heartbeats), la emisión de miniaturas en vivo (thumbnails)
/// y el control del reproductor LibVLC en modo kiosco.
/// </summary>
public class AgentService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<AgentService> _logger;
    private readonly VlcPlayerManager _playerManager;
    private readonly FileService _fileService;
    private readonly SystemCommandService _systemCommands;
    private readonly VncRemoteControlService _remoteControl;
    private readonly AgentTokenProvider _tokens;

    private HubConnection? _hubConnection;

    public AgentService(
        IConfiguration config,
        ILogger<AgentService> logger,
        FileService fileService,
        SystemCommandService systemCommands,
        VncRemoteControlService remoteControl,
        AgentTokenProvider tokens)
    {
        _config = config;
        _logger = logger;
        _fileService = fileService;
        _systemCommands = systemCommands;
        _remoteControl = remoteControl;
        _tokens = tokens;
        _playerManager = new VlcPlayerManager(config);

        // El túnel VNC suspende el kiosco mientras el operador usa el escritorio.
        _remoteControl.Kiosk = _playerManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var screenId = _config["Agent:ScreenId"] ?? "screen-01";
        var screenName = _config["Agent:Name"] ?? $"Pantalla {screenId}";
        var token = _config["Agent:Token"] ?? "dev-token";
        var serverUrl = _config["Agent:ServerUrl"] ?? "http://localhost:5000";

        AgentLog.Info("==================================================");
        AgentLog.Info($"   PCP AGENT - PANTALLA: {screenId} ({screenName})");
        AgentLog.Info($"   Servidor: {serverUrl}");
        AgentLog.Info($"   Reproductor Kiosco: LibVLC (Hardware Acceleration)");
        AgentLog.Info($"   Control remoto VNC: {(_remoteControl.IsAvailable ? "disponible" : "NO disponible")}");
        AgentLog.Info("==================================================");

        // 1. Intentar pre-registro REST en el servidor
        await TryRegisterRestAsync(serverUrl, screenId, screenName, token, stoppingToken);

        // 2. Configuración de conexión SignalR con política de reconexión automática.
        // La identidad viaja en el JWT obtenido tras el registro; SignalR lo adjunta
        // en cada (re)conexión, incluida la query access_token del WebSocket.
        var initialToken = await _tokens.GetTokenAsync(stoppingToken);
        if (string.IsNullOrEmpty(initialToken))
        {
            AgentLog.Warn("Aún no hay token JWT de agente. Se reintentará al conectar.");
        }

        // Se conservan screenId/token en la query para que este agente también pueda
        // conectarse a un servidor de Fase 1/2 durante una actualización escalonada.
        // El servidor nuevo autentica por JWT (access_token) e ignora estos parámetros.
        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/screen" +
                     $"?screenId={Uri.EscapeDataString(screenId)}" +
                     $"&token={Uri.EscapeDataString(token)}";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => _tokens.GetTokenAsync();
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20)
            })
            .Build();

        // 3. Suscripción a eventos de ciclo de vida de la conexión
        _hubConnection.Reconnecting += error =>
        {
            AgentLog.Warn($"[ALERTA] Conexión perdida con el servidor. Reconectando... ({error?.Message})");
            // Fuerza una renovación por si el servidor rotó la clave de firma.
            _tokens.Invalidate();
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            AgentLog.Info($"[CONECTADO] Reconexión exitosa al Hub. Nuevo ConnectionId: {connectionId}");
            return Task.CompletedTask;
        };

        _hubConnection.Closed += error =>
        {
            AgentLog.Error($"[ERROR] Conexión cerrada. {error?.Message}");
            _tokens.Invalidate();
            return Task.CompletedTask;
        };

        // 4. Registro de manejador para comandos enviados por el servidor
        _hubConnection.On<CommandDto>("SendCommandToAgent", async (command) =>
        {
            await ProcessCommandAsync(command, screenId);
        });

        // 4b. Registro de manejador para operaciones del explorador de archivos
        _hubConnection.On<FileRequestDto>("FileRequest", async (request) =>
        {
            await ProcessFileRequestAsync(request, screenId);
        });

        // 4c. Registro de manejador para el escritorio remoto (túnel VNC)
        _remoteControl.SetStatusReporter(async status =>
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                await _hubConnection.InvokeAsync(RemoteControlEvents.TunnelStatus, status);
            }
        });

        _hubConnection.On<VncTunnelRequestDto>(RemoteControlEvents.OpenTunnel, request =>
        {
            AgentLog.Info($"[VNC] Petición de control remoto recibida (sesión {request.SessionId})");
            _remoteControl.Start(request);
        });

        _hubConnection.On<string>(RemoteControlEvents.CloseTunnel, sessionId =>
        {
            _remoteControl.Stop(sessionId);
        });

        // 5. Iniciar conexión al Hub con reintentos hasta que el servidor esté disponible
        await ConnectWithRetryAsync(stoppingToken);

        // 6. Bucle de emisión de miniaturas (Thumbnails) cada 5 segundos
        _ = Task.Run(async () =>
        {
            var thumbInterval = TimeSpan.FromSeconds(5);

            // Emisión inicial inmediata al conectar
            try
            {
                await EmitThumbnailAsync(screenId);
            }
            catch { }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(thumbInterval, stoppingToken);
                    await EmitThumbnailAsync(screenId);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error capturando miniatura: {Msg}", ex.Message);
                }
            }
        }, stoppingToken);

        // 7. Bucle de emisión de Heartbeat cada 10 segundos
        var heartbeatInterval = TimeSpan.FromSeconds(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            await EmitHeartbeatAsync(screenId);
            try
            {
                await Task.Delay(heartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_hubConnection != null)
        {
            await _hubConnection.StopAsync(CancellationToken.None);
            await _hubConnection.DisposeAsync();
        }

        try
        {
            await _remoteControl.StopAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error cerrando el control remoto: {Message}", ex.Message);
        }

        _playerManager.Dispose();
    }

    /// <summary>
    /// Intenta registrar o verificar la pantalla mediante la API REST previa a la conexión del Hub.
    /// </summary>
    private async Task TryRegisterRestAsync(string serverUrl, string screenId, string name, string token, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var registerDto = new RegisterDto
        {
            ScreenId = screenId,
            Name = name,
            Token = token
        };

        var json = JsonSerializer.Serialize(registerDto);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await client.PostAsync($"{serverUrl.TrimEnd('/')}/api/screens/register", content, ct);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Registro REST confirmado por el servidor.");
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    AgentLog.Error(
                        "El servidor rechazó el registro: el token de la pantalla no coincide. " +
                        "Revise Agent:Token / PCP_TOKEN.");
                    return;
                }
            }
            catch
            {
                // Reintento en caso de inicio desfasado del servidor
            }

            if (attempt < 3)
            {
                await Task.Delay(1500, ct);
            }
        }
    }

    /// <summary>
    /// Intenta conectar al Hub SignalR repitiendo en caso de que el servidor aún no haya iniciado.
    /// </summary>
    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                attempt++;
                AgentLog.Info($"Conectando al Hub SignalR (intento #{attempt})...");
                await _hubConnection!.StartAsync(ct);

                AgentLog.Info($"[CONECTADO] Conectado exitosamente al servidor SignalR.");

                // Enviar inmediatamente un thumbnail inicial
                await EmitThumbnailAsync(_config["Agent:ScreenId"] ?? "screen-01");
                return;
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                {
                    // El servidor rotó la clave o revocó el token: se renueva en el próximo intento.
                    _tokens.Invalidate();
                }

                AgentLog.Warn($"Servidor aún no disponible ({ex.Message}). Reintentando en 3s...");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Captura y emite la miniatura (fotograma o estado standby) hacia el servidor vía SignalR.
    /// </summary>
    private async Task EmitThumbnailAsync(string screenId)
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        try
        {
            var thumbnailBase64 = _playerManager.CaptureThumbnailBase64(screenId);
            if (!string.IsNullOrEmpty(thumbnailBase64))
            {
                var dto = new ThumbnailDto
                {
                    ScreenId = screenId,
                    ThumbnailBase64 = thumbnailBase64,
                    Timestamp = DateTime.UtcNow
                };

                await _hubConnection.InvokeAsync("SendThumbnail", dto);
                AgentLog.Info($"[THUMBNAIL] Miniatura transmitida ({thumbnailBase64.Length} chars)");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"[THUMBNAIL ERROR] {ex.Message}");
        }
    }

    /// <summary>
    /// Recolecta métricas del sistema y emite el latido de telemetría hacia el servidor.
    /// </summary>
    private async Task EmitHeartbeatAsync(string screenId)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
        {
            return;
        }

        var (cpu, ram, freeDiskMb) = SampleSystemMetrics();
        var currentMedia = _playerManager.CurrentMedia;
        var volume = _playerManager.Volume;

        var heartbeat = new HeartbeatDto
        {
            ScreenId = screenId,
            Timestamp = DateTime.UtcNow,
            CurrentMedia = currentMedia,
            Volume = volume,
            CpuPercent = cpu,
            RamPercent = ram,
            FreeDiskMb = freeDiskMb,
            HdmiOk = true,
            RemoteControlAvailable = _remoteControl.IsAvailable
        };

        try
        {
            await _hubConnection.InvokeAsync("Heartbeat", heartbeat);
            AgentLog.Info($"[HEARTBEAT] Enviado: Media='{currentMedia}', Vol={volume}%, CPU={cpu:F1}%, RAM={ram:F1}%, Disco={freeDiskMb}MB");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error al emitir heartbeat: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Obtiene mediciones o estimaciones de telemetría del hardware local.
    /// </summary>
    private static (double cpu, double ram, long freeDiskMb) SampleSystemMetrics()
    {
        var random = new Random();
        double cpu = Math.Round(random.NextDouble() * 25 + 5, 1);

        double ram = 35.0;
        try
        {
            var proc = Process.GetCurrentProcess();
            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.TotalAvailableMemoryBytes > 0)
            {
                ram = Math.Round(((double)proc.WorkingSet64 / memoryInfo.TotalAvailableMemoryBytes) * 100, 1);
            }
        }
        catch
        {
            ram = Math.Round(random.NextDouble() * 20 + 25, 1);
        }

        long freeDiskMb = 50000;
        try
        {
            var systemDrive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
            freeDiskMb = systemDrive.AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            freeDiskMb = 45000;
        }

        return (cpu, ram, freeDiskMb);
    }

    /// <summary>
    /// Ejecuta una operación del explorador de archivos y devuelve el resultado al servidor.
    /// Los chunks de subida no generan respuesta individual; el cierre de la subida sí.
    /// </summary>
    private async Task ProcessFileRequestAsync(FileRequestDto request, string screenId)
    {
        FileResultDto? result = null;

        try
        {
            switch (request.Operation)
            {
                case FileOperations.List:
                    result = _fileService.List(request);
                    break;
                case FileOperations.Delete:
                    result = _fileService.Delete(request);
                    break;
                case FileOperations.UploadStart:
                    result = _fileService.StartUpload(request);
                    // El inicio correcto no se responde: la confirmación llega al cerrar la subida.
                    // Solo se devuelve un resultado si el agente rechazó la operación.
                    if (result.Success)
                    {
                        result = null;
                    }
                    break;
                case FileOperations.UploadChunk:
                    _fileService.AppendChunk(request);
                    break;
                case FileOperations.UploadComplete:
                    result = _fileService.CompleteUpload(request);
                    break;
                case FileOperations.UploadCancel:
                    _fileService.CancelUpload(request.RequestId);
                    break;
                default:
                    result = new FileResultDto
                    {
                        RequestId = request.RequestId,
                        ScreenId = screenId,
                        Success = false,
                        Error = $"Operación de archivos no soportada: '{request.Operation}'."
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al procesar la operación de archivos '{Operation}'", request.Operation);
            result = new FileResultDto
            {
                RequestId = request.RequestId,
                ScreenId = screenId,
                Success = false,
                Error = $"Error interno en el agente: {ex.Message}"
            };
        }

        if (result == null)
        {
            return;
        }

        result.ScreenId = screenId;

        if (_hubConnection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _hubConnection.InvokeAsync("FileResult", result);
            AgentLog.Info($"[FILES] {request.Operation}: {(result.Success ? "OK" : result.Error)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo enviar el resultado de archivos al servidor");
        }
    }

    /// <summary>
    /// Ejecuta la lógica del comando recibido sobre el reproductor LibVLC y reporta el resultado.
    /// </summary>
    private async Task ProcessCommandAsync(CommandDto command, string screenId)
    {
        AgentLog.Info($"\n==================================================");
        AgentLog.Info($"▶ COMANDO RECIBIDO: {command.Type.ToUpper()} (ID: {command.CommandId})");
        AgentLog.Info($"==================================================");

        string status = "ok";
        string detail = string.Empty;

        try
        {
            switch (command.Type.ToLowerInvariant())
            {
                case "play":
                    PlayPayload? playPayload = null;
                    if (!string.IsNullOrWhiteSpace(command.PayloadJson))
                    {
                        try
                        {
                            playPayload = JsonSerializer.Deserialize<PlayPayload>(command.PayloadJson,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Error deserializando PlayPayload: {Message}", ex.Message);
                        }
                    }

                    var mediaPath = playPayload?.MediaPath ?? "C:\\videos\\demo.mp4";
                    var loop = playPayload?.Loop ?? true;
                    var volume = playPayload?.Volume ?? _playerManager.Volume;

                    // Reproducir con LibVLC
                    _playerManager.Play(mediaPath, loop, volume);

                    AgentLog.Info($"▶ [PLAY VLC] Reproduciendo: {mediaPath}");
                    AgentLog.Info($"   Loop: {loop} | Volumen: {volume}% | Kiosk Mode");

                    detail = $"Reproduciendo medio con LibVLC: {mediaPath}";

                    // Forzar emisión inmediata de miniatura
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await EmitThumbnailAsync(screenId);
                    });
                    break;

                case "pause":
                    _playerManager.Pause();
                    AgentLog.Info($"⏸ [PAUSE VLC] Reproducción alternada en: {_playerManager.CurrentMedia}");
                    detail = $"Reproducción pausada/reanudada en: {_playerManager.CurrentMedia}";
                    break;

                case "stop":
                    _playerManager.Stop();
                    AgentLog.Info($"⏹ [STOP VLC] Reproducción detenida. Estado cambiado a reposo (idle).");
                    detail = "Reproducción detenida.";

                    // Emitir inmediatamente miniatura de reposo
                    await EmitThumbnailAsync(screenId);
                    break;

                case "volume":
                    int newVol = _playerManager.Volume;
                    if (!string.IsNullOrWhiteSpace(command.PayloadJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(command.PayloadJson);
                            if (doc.RootElement.TryGetProperty("volume", out var p) ||
                                doc.RootElement.TryGetProperty("Volume", out p))
                            {
                                newVol = p.GetInt32();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Error leyendo volumen de payload: {Message}", ex.Message);
                        }
                    }

                    _playerManager.SetVolume(newVol);
                    AgentLog.Info($"🔊 [VOLUME VLC] Volumen ajustado a: {newVol}%");
                    detail = $"Volumen ajustado a {newVol}%";
                    break;

                case "shutdown":
                    var shutdownResult = _systemCommands.Shutdown();
                    status = shutdownResult.Success ? "ok" : "error";
                    detail = shutdownResult.Detail;
                    break;

                case "restart":
                    var restartResult = _systemCommands.Restart();
                    status = restartResult.Success ? "ok" : "error";
                    detail = restartResult.Detail;
                    break;

                case "monitor-off":
                case "monitoroff":
                case "monitor_off":
                    var monitorResult = _systemCommands.MonitorOff();
                    status = monitorResult.Success ? "ok" : "error";
                    detail = monitorResult.Detail;
                    break;

                case "lock":
                case "lock-session":
                    var lockResult = _systemCommands.LockSession();
                    status = lockResult.Success ? "ok" : "error";
                    detail = lockResult.Detail;
                    break;

                case "kiosk-pause":
                case "kiosk_pause":
                case "pause_kiosk":
                case "pausekiosk":
                    _playerManager.Suspend(VlcPlayerManager.ManualSuspendReason);
                    AgentLog.Info("🖥 [KIOSK] Kiosco suspendido manualmente: escritorio libre.");
                    detail = "Kiosco suspendido: el reproductor queda minimizado y sin TopMost.";
                    break;

                case "kiosk-resume":
                case "kiosk_resume":
                case "resume_kiosk":
                case "resumekiosk":
                    _playerManager.Resume(VlcPlayerManager.ManualSuspendReason);
                    AgentLog.Info("🖥 [KIOSK] Kiosco reanudado manualmente.");
                    detail = _playerManager.HasMedia
                        ? "Kiosco reanudado: el vídeo vuelve a pantalla completa."
                        : "Kiosco reanudado en reposo.";
                    break;

                default:
                    status = "error";
                    detail = $"Comando no reconocido o no soportado: '{command.Type}'";
                    AgentLog.Error($"✖ [ERROR] {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            status = "error";
            detail = $"Excepción al ejecutar comando: {ex.Message}";
            AgentLog.Error($"✖ [EXCEPCIÓN] {detail}");
        }

        // Devolver respuesta con CommandResultDto
        var resultDto = new CommandResultDto
        {
            CommandId = command.CommandId,
            ScreenId = screenId,
            Status = status,
            Detail = detail,
            Timestamp = DateTime.UtcNow
        };

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("CommandResult", resultDto);
                AgentLog.Info($"✓ [CONFIRMACIÓN] Resultado reportado al servidor: Status='{status}', Detalle='{detail}'\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al enviar CommandResult al servidor");
            }
        }
    }
}
