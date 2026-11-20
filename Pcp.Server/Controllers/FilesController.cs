using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// API REST para el explorador de archivos remoto de una pantalla.
/// Las operaciones se delegan al agente mediante SignalR.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Viewer")]
[Route("api/screens/{screenId}/files")]
public class FilesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IScreenConnectionManager _connectionManager;
    private readonly IFileRequestManager _fileRequests;
    private readonly ILogger<FilesController> _logger;
    private readonly long _maxUploadBytes;

    public FilesController(
        AppDbContext db,
        IScreenConnectionManager connectionManager,
        IFileRequestManager fileRequests,
        IConfiguration configuration,
        ILogger<FilesController> logger)
    {
        _db = db;
        _connectionManager = connectionManager;
        _fileRequests = fileRequests;
        _logger = logger;

        var maxUploadMb = configuration.GetValue<long?>("Files:MaxUploadMb") ?? 200;
        _maxUploadBytes = Math.Max(1, maxUploadMb) * 1024L * 1024L;
    }

    /// <summary>
    /// GET /api/screens/{screenId}/files?path=C:\videos
    /// Lista las unidades (sin path) o el contenido de una carpeta.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        string screenId,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        var invalid = await ValidateScreenAsync(screenId, cancellationToken);
        if (invalid != null)
        {
            return invalid;
        }

        var result = await _fileRequests.SendRequestAsync(
            screenId,
            new FileRequestDto
            {
                Operation = FileOperations.List,
                Path = path
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!result.Success || result.Listing == null)
        {
            return BadRequest(new { error = result.Error ?? "No se pudo listar la carpeta." });
        }

        return Ok(result.Listing);
    }

    /// <summary>
    /// DELETE /api/screens/{screenId}/files?path=C:\videos\archivo.mp4
    /// Elimina un archivo (no carpetas).
    /// </summary>
    [HttpDelete]
    [Authorize(Roles = TokenService.AdminRole)]
    public async Task<IActionResult> Delete(
        string screenId,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "El parámetro 'path' es obligatorio." });
        }

        var invalid = await ValidateScreenAsync(screenId, cancellationToken);
        if (invalid != null)
        {
            return invalid;
        }

        var result = await _fileRequests.SendRequestAsync(
            screenId,
            new FileRequestDto
            {
                Operation = FileOperations.Delete,
                Path = path
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "No se pudo eliminar el archivo." });
        }

        _logger.LogInformation("Archivo eliminado en pantalla '{ScreenId}': {Path}", screenId, path);
        return Ok(new { success = true });
    }

    /// <summary>
    /// POST /api/screens/{screenId}/files/upload?path=C:\videos
    /// Sube un archivo a la carpeta indicada del agente (multipart/form-data).
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Roles = TokenService.AdminRole)]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> Upload(
        string screenId,
        [FromQuery] string? path,
        [FromQuery] string? clientRequestId,
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "La carpeta de destino ('path') es obligatoria." });
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No se recibió ningún archivo." });
        }

        if (file.Length > _maxUploadBytes)
        {
            return BadRequest(new { error = $"El archivo excede el límite de {_maxUploadBytes / (1024 * 1024)} MB." });
        }

        var invalid = await ValidateScreenAsync(screenId, cancellationToken);
        if (invalid != null)
        {
            return invalid;
        }

        await using var stream = file.OpenReadStream();

        var result = await _fileRequests.UploadAsync(
            screenId,
            path,
            file.FileName,
            file.Length,
            stream,
            TimeSpan.FromMinutes(5),
            clientRequestId,
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "No se pudo subir el archivo." });
        }

        _logger.LogInformation("Archivo subido a pantalla '{ScreenId}': {Path}", screenId, result.SavedPath);
        return Ok(new { success = true, savedPath = result.SavedPath });
    }

    private async Task<IActionResult?> ValidateScreenAsync(string screenId, CancellationToken cancellationToken)
    {
        var exists = await _db.Screens
            .AsNoTracking()
            .AnyAsync(s => s.Id == screenId, cancellationToken);

        if (!exists)
        {
            return NotFound(new { error = $"Pantalla '{screenId}' no encontrada." });
        }

        if (string.IsNullOrEmpty(_connectionManager.GetConnectionId(screenId)))
        {
            return BadRequest(new { error = $"La pantalla '{screenId}' no está conectada." });
        }

        return null;
    }
}
