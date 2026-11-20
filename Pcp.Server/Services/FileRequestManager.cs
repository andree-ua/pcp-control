using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pcp.Server.Hubs;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Correlaciona solicitudes de archivos enviadas a los agentes con sus respuestas
/// asíncronas recibidas a través del Hub SignalR.
/// </summary>
public interface IFileRequestManager
{
    Task<FileResultDto> SendRequestAsync(
        string screenId,
        FileRequestDto request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<FileResultDto> UploadAsync(
        string screenId,
        string directoryPath,
        string fileName,
        long totalBytes,
        Stream stream,
        TimeSpan timeout,
        string? clientRequestId = null,
        CancellationToken cancellationToken = default);

    void Complete(FileResultDto result);

    void CancelPending(string screenId, string reason);
}

public class FileRequestManager : IFileRequestManager
{
    private const int UploadChunkBytes = 16 * 1024;
    private const int ProgressThrottleMs = 250;

    private readonly IHubContext<ScreenHub> _hubContext;
    private readonly IScreenConnectionManager _connectionManager;
    private readonly ILogger<FileRequestManager> _logger;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();

    public FileRequestManager(
        IHubContext<ScreenHub> hubContext,
        IScreenConnectionManager connectionManager,
        ILogger<FileRequestManager> logger)
    {
        _hubContext = hubContext;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<FileResultDto> SendRequestAsync(
        string screenId,
        FileRequestDto request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (request.RequestId == Guid.Empty)
        {
            request.RequestId = Guid.NewGuid();
        }

        request.ScreenId = screenId;

        var connectionId = _connectionManager.GetConnectionId(screenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return Failure(request, "La pantalla no está conectada al servidor.");
        }

        var pending = new PendingRequest(screenId);
        _pending[request.RequestId] = pending;

        try
        {
            await _hubContext.Clients.Client(connectionId)
                .SendAsync("FileRequest", request, cancellationToken);

            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Failure(request, "El agente no respondió a tiempo.");
        }
        catch (OperationCanceledException)
        {
            return Failure(request, "La operación fue cancelada.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al solicitar la operación '{Operation}' a la pantalla {ScreenId}",
                request.Operation, screenId);
            return Failure(request, $"Error al comunicarse con el agente: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public async Task<FileResultDto> UploadAsync(
        string screenId,
        string directoryPath,
        string fileName,
        long totalBytes,
        Stream stream,
        TimeSpan timeout,
        string? clientRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new FileRequestDto
        {
            RequestId = Guid.NewGuid(),
            ScreenId = screenId,
            Operation = FileOperations.UploadStart,
            Path = directoryPath,
            FileName = fileName,
            TotalBytes = totalBytes
        };

        var connectionId = _connectionManager.GetConnectionId(screenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return Failure(request, "La pantalla no está conectada al servidor.");
        }

        var pending = new PendingRequest(screenId);
        _pending[request.RequestId] = pending;
        var proxy = _hubContext.Clients.Client(connectionId);
        long sentBytes = 0;
        var chunkIndex = 0;
        var lastProgressAt = Environment.TickCount64;

        try
        {
            await proxy.SendAsync("FileRequest", request, cancellationToken);

            // Si el agente rechazó el inicio (ruta inválida, archivo existente, etc.).
            if (pending.Completion.Task.IsCompleted)
            {
                var rejected = await pending.Completion.Task;
                ReportProgress(clientRequestId, screenId, fileName, 0, totalBytes, 0,
                    UploadPhases.Failed, rejected.Error);
                return rejected;
            }

            ReportProgress(clientRequestId, screenId, fileName, 0, totalBytes, 0, UploadPhases.Uploading);

            var buffer = new byte[UploadChunkBytes];
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (pending.Completion.Task.IsCompleted)
                {
                    await SendCancelAsync(proxy, request);
                    var failed = await pending.Completion.Task;
                    ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                        UploadPhases.Failed, failed.Error);
                    return failed;
                }

                var chunk = new FileRequestDto
                {
                    RequestId = request.RequestId,
                    ScreenId = screenId,
                    Operation = FileOperations.UploadChunk,
                    ChunkIndex = chunkIndex++,
                    DataBase64 = Convert.ToBase64String(buffer, 0, read)
                };

                await proxy.SendAsync("FileRequest", chunk, cancellationToken);

                sentBytes += read;

                // Limitar a ~4 eventos/s: un archivo de 200 MB son >12.000 chunks.
                var now = Environment.TickCount64;
                if (now - lastProgressAt >= ProgressThrottleMs)
                {
                    lastProgressAt = now;
                    ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                        UploadPhases.Uploading);
                }
            }

            ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                UploadPhases.Uploading);

            await proxy.SendAsync("FileRequest", new FileRequestDto
            {
                RequestId = request.RequestId,
                ScreenId = screenId,
                Operation = FileOperations.UploadComplete,
                ChunkIndex = chunkIndex,
                TotalBytes = totalBytes
            }, cancellationToken);

            var result = await pending.Completion.Task.WaitAsync(timeout, cancellationToken);

            ReportProgress(
                clientRequestId,
                screenId,
                fileName,
                sentBytes,
                totalBytes,
                chunkIndex,
                result.Success ? UploadPhases.Completed : UploadPhases.Failed,
                result.Success ? null : result.Error);

            return result;
        }
        catch (TimeoutException)
        {
            await SendCancelAsync(proxy, request);
            ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                UploadPhases.Failed, "El agente no confirmó la subida a tiempo.");
            return Failure(request, "El agente no confirmó la subida a tiempo.");
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync(proxy, request);
            ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                UploadPhases.Failed, "La subida fue cancelada.");
            return Failure(request, "La subida fue cancelada.");
        }
        catch (Exception ex)
        {
            await SendCancelAsync(proxy, request);
            ReportProgress(clientRequestId, screenId, fileName, sentBytes, totalBytes, chunkIndex,
                UploadPhases.Failed, ex.Message);
            _logger.LogWarning(ex, "Error al subir un archivo a la pantalla {ScreenId}", screenId);
            return Failure(request, $"Error al subir el archivo: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    /// <summary>
    /// Emite el progreso del tramo servidor → agente al grupo "panel". Es best-effort:
    /// un fallo al notificar nunca debe romper la subida.
    /// </summary>
    private void ReportProgress(
        string? clientRequestId,
        string screenId,
        string fileName,
        long sentBytes,
        long totalBytes,
        int chunkIndex,
        string phase,
        string? error = null)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId))
        {
            return;
        }

        var dto = new FileUploadProgressDto
        {
            ClientRequestId = clientRequestId,
            ScreenId = screenId,
            FileName = fileName,
            SentBytes = sentBytes,
            TotalBytes = totalBytes,
            ChunkIndex = chunkIndex,
            Phase = phase,
            Error = error
        };

        _ = _hubContext.Clients.Group("panel")
            .SendAsync(FileTransferEvents.UploadProgress, dto)
            .ContinueWith(
                task => _logger.LogDebug("No se pudo emitir el progreso de subida: {Message}",
                    task.Exception?.Message),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Complete(FileResultDto result)
    {
        if (_pending.TryGetValue(result.RequestId, out var pending))
        {
            pending.Completion.TrySetResult(result);
            return;
        }

        _logger.LogDebug("Resultado de archivos sin solicitud pendiente: {RequestId}", result.RequestId);
    }

    public void CancelPending(string screenId, string reason)
    {
        foreach (var item in _pending)
        {
            if (!string.Equals(item.Value.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.Value.Completion.TrySetResult(new FileResultDto
            {
                RequestId = item.Key,
                ScreenId = screenId,
                Success = false,
                Error = reason
            });
        }
    }

    private static async Task SendCancelAsync(ISingleClientProxy proxy, FileRequestDto request)
    {
        try
        {
            await proxy.SendAsync("FileRequest", new FileRequestDto
            {
                RequestId = request.RequestId,
                ScreenId = request.ScreenId,
                Operation = FileOperations.UploadCancel
            });
        }
        catch
        {
            // El agente pudo haberse desconectado; la limpieza es best-effort.
        }
    }

    private static FileResultDto Failure(FileRequestDto request, string error) => new()
    {
        RequestId = request.RequestId,
        ScreenId = request.ScreenId,
        Success = false,
        Error = error
    };

    private sealed class PendingRequest
    {
        public PendingRequest(string screenId)
        {
            ScreenId = screenId;
        }

        public string ScreenId { get; }

        public TaskCompletionSource<FileResultDto> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
