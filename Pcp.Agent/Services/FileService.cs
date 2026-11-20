using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Servicio de gestión de archivos del agente. Todas las rutas se canonicalizan y
/// se validan contra las raíces permitidas y la lista negra de rutas críticas.
/// </summary>
public class FileService
{
    private const int MaxEntries = 2000;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v",
        ".mpg", ".mpeg", ".wmv", ".flv", ".ts"
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly string[] DefaultProtectedPaths =
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
        @"C:\$Recycle.Bin",
        @"C:\System Volume Information",
        @"C:\Recovery",
        @"C:\Boot",
        @"C:\PerfLogs",
        @"C:\pagefile.sys",
        @"C:\hiberfil.sys",
        @"C:\swapfile.sys"
    };

    private readonly ILogger<FileService> _logger;
    private readonly List<string> _roots;
    private readonly List<string> _protectedPaths;
    private readonly long _maxUploadBytes;
    private readonly ConcurrentDictionary<Guid, UploadSession> _uploads = new();

    public FileService(IConfiguration configuration, ILogger<FileService> logger)
    {
        _logger = logger;

        var configuredRoots = configuration.GetSection("Agent:FileRoots").Get<string[]>();
        _roots = new List<string>();

        if (configuredRoots is { Length: > 0 })
        {
            foreach (var root in configuredRoots)
            {
                try
                {
                    _roots.Add(NormalizeWithSeparator(root));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Raíz de archivos inválida '{Root}': {Message}", root, ex.Message);
                }
            }
        }

        if (_roots.Count == 0)
        {
            _roots = DetectFixedDrives();
        }

        var configuredProtected = configuration.GetSection("Agent:ProtectedPaths").Get<string[]>();
        var protectedSource = configuredProtected is { Length: > 0 }
            ? configuredProtected
            : DefaultProtectedPaths;

        _protectedPaths = new List<string>();
        foreach (var path in protectedSource)
        {
            try
            {
                _protectedPaths.Add(NormalizeWithSeparator(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Ruta protegida inválida '{Path}': {Message}", path, ex.Message);
            }
        }

        var maxUploadMb = configuration.GetValue<long?>("Agent:MaxUploadMb") ?? 200;
        _maxUploadBytes = Math.Max(1, maxUploadMb) * 1024L * 1024L;
    }

    /// <summary>Lista una carpeta o, si la ruta viene vacía, las unidades disponibles.</summary>
    public FileResultDto List(FileRequestDto request)
    {
        var result = NewResult(request);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            result.Success = true;
            result.Listing = new FileListResultDto
            {
                Path = string.Empty,
                ParentPath = null,
                Roots = GetRoots(),
                Entries = new List<FileEntryDto>(),
                IsRoot = true
            };
            return result;
        }

        if (!TryResolveDirectory(request.Path, out var fullPath, out var error))
        {
            result.Error = error;
            return result;
        }

        try
        {
            var directory = new DirectoryInfo(fullPath);
            var entries = new List<FileEntryDto>();
            var truncated = false;

            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    var attributes = info.Attributes;

                    // No exponer enlaces simbólicos/junctions: podrían escapar de las raíces permitidas.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var extension = isDirectory ? string.Empty : info.Extension.ToLowerInvariant();

                    entries.Add(new FileEntryDto
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        IsDirectory = isDirectory,
                        SizeBytes = isDirectory ? 0 : ((FileInfo)info).Length,
                        LastModified = info.LastWriteTimeUtc,
                        Extension = extension,
                        IsVideo = !isDirectory && VideoExtensions.Contains(extension)
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("No se pudo leer la entrada '{Name}': {Message}", info.Name, ex.Message);
                }
            }

            entries.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            result.Success = true;
            result.Listing = new FileListResultDto
            {
                Path = fullPath,
                ParentPath = GetParentWithinRoots(fullPath),
                Roots = GetRoots(),
                Entries = entries,
                IsRoot = IsDriveRoot(fullPath),
                Truncated = truncated
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al listar la carpeta '{Path}'", fullPath);
            result.Error = $"No se pudo leer la carpeta: {ex.Message}";
        }

        return result;
    }

    /// <summary>Elimina un archivo (nunca una carpeta).</summary>
    public FileResultDto Delete(FileRequestDto request)
    {
        var result = NewResult(request);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            result.Error = "La ruta del archivo es obligatoria.";
            return result;
        }

        if (!TryResolveFile(request.Path, out var fullPath, out var error))
        {
            result.Error = error;
            return result;
        }

        try
        {
            File.Delete(fullPath);
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al eliminar el archivo '{Path}'", fullPath);
            result.Error = $"No se pudo eliminar el archivo: {ex.Message}";
            return result;
        }
    }

    /// <summary>Valida el destino y crea la sesión temporal de subida.</summary>
    public FileResultDto StartUpload(FileRequestDto request)
    {
        var result = NewResult(request);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            result.Error = "La carpeta de destino es obligatoria.";
            return result;
        }

        if (!TryResolveDirectory(request.Path, out var directoryPath, out var error))
        {
            result.Error = error;
            return result;
        }

        var fileName = SanitizeFileName(request.FileName);
        if (fileName == null)
        {
            result.Error = "El nombre del archivo no es válido.";
            return result;
        }

        if (request.TotalBytes <= 0 || request.TotalBytes > _maxUploadBytes)
        {
            result.Error = $"El tamaño del archivo excede el límite permitido ({_maxUploadBytes / (1024 * 1024)} MB).";
            return result;
        }

        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(directoryPath, fileName));
        }
        catch
        {
            result.Error = "La ruta de destino no es válida.";
            return result;
        }

        if (!IsUnderAllowedRoot(targetPath) || IsProtected(targetPath))
        {
            result.Error = "La ruta de destino no está permitida.";
            return result;
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            result.Error = $"El archivo '{fileName}' ya existe. No se permite sobrescribir.";
            return result;
        }

        var tempPath = Path.Combine(directoryPath, $".pcp-upload-{request.RequestId:N}.tmp");

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan);

            var session = new UploadSession
            {
                TempPath = tempPath,
                TargetPath = targetPath,
                ExpectedBytes = request.TotalBytes,
                Stream = stream
            };

            if (!_uploads.TryAdd(request.RequestId, session))
            {
                stream.Dispose();
                TryDelete(tempPath);
                result.Error = "Ya existe una subida con ese identificador.";
                return result;
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al iniciar la subida en '{Path}'", directoryPath);
            result.Error = $"No se pudo iniciar la subida: {ex.Message}";
            return result;
        }
    }

    /// <summary>Añade un bloque Base64 a la sesión de subida. No genera respuesta.</summary>
    public void AppendChunk(FileRequestDto request)
    {
        if (!_uploads.TryGetValue(request.RequestId, out var session))
        {
            return;
        }

        lock (session.Sync)
        {
            if (session.Error != null)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(request.DataBase64))
                {
                    session.Error = "El bloque de datos está vacío.";
                    return;
                }

                var data = Convert.FromBase64String(request.DataBase64);
                if (session.BytesWritten + data.Length > session.ExpectedBytes)
                {
                    session.Error = "El archivo recibido supera el tamaño declarado.";
                    return;
                }

                session.Stream.Write(data, 0, data.Length);
                session.BytesWritten += data.Length;
            }
            catch (Exception ex)
            {
                session.Error = $"Error al escribir el bloque de datos: {ex.Message}";
                _logger.LogWarning(ex, "Error al escribir chunk de subida {RequestId}", request.RequestId);
            }
        }
    }

    /// <summary>Cierra la sesión y mueve el archivo temporal a su destino final.</summary>
    public FileResultDto CompleteUpload(FileRequestDto request)
    {
        var result = NewResult(request);

        if (!_uploads.TryRemove(request.RequestId, out var session))
        {
            result.Error = "La sesión de subida no existe o ya fue cerrada.";
            return result;
        }

        lock (session.Sync)
        {
            try
            {
                try
                {
                    session.Stream.Flush(true);
                }
                catch
                {
                    // El dispose posterior reportará el error real si existe.
                }

                session.Stream.Dispose();

                if (session.Error != null)
                {
                    TryDelete(session.TempPath);
                    result.Error = session.Error;
                    return result;
                }

                if (session.BytesWritten != session.ExpectedBytes)
                {
                    TryDelete(session.TempPath);
                    result.Error = $"Subida incompleta: se esperaban {session.ExpectedBytes} bytes y se recibieron {session.BytesWritten}.";
                    return result;
                }

                if (File.Exists(session.TargetPath) || Directory.Exists(session.TargetPath))
                {
                    TryDelete(session.TempPath);
                    result.Error = "El archivo ya existe en el destino. No se permite sobrescribir.";
                    return result;
                }

                File.Move(session.TempPath, session.TargetPath);
                result.Success = true;
                result.SavedPath = session.TargetPath;
                _logger.LogInformation("Archivo subido correctamente: '{Path}' ({Bytes} bytes)", session.TargetPath, session.BytesWritten);
                return result;
            }
            catch (Exception ex)
            {
                TryDelete(session.TempPath);
                _logger.LogWarning(ex, "Error al finalizar la subida de '{Path}'", session.TargetPath);
                result.Error = $"No se pudo guardar el archivo: {ex.Message}";
                return result;
            }
        }
    }

    /// <summary>Descarta una subida en curso y elimina el archivo temporal.</summary>
    public void CancelUpload(Guid requestId)
    {
        if (!_uploads.TryRemove(requestId, out var session))
        {
            return;
        }

        lock (session.Sync)
        {
            try
            {
                session.Stream.Dispose();
            }
            catch
            {
                // Limpieza best-effort.
            }

            TryDelete(session.TempPath);
        }
    }

    private FileResultDto NewResult(FileRequestDto request) => new()
    {
        RequestId = request.RequestId,
        ScreenId = request.ScreenId
    };

    private List<string> GetRoots() => _roots.Select(TrimTrailingSeparator).ToList();

    private bool TryResolveDirectory(string rawPath, out string fullPath, out string error)
    {
        if (!TryValidatePath(rawPath, out fullPath, out error))
        {
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = "La carpeta no existe.";
            return false;
        }

        fullPath = TrimTrailingSeparator(fullPath);
        return true;
    }

    private bool TryResolveFile(string rawPath, out string fullPath, out string error)
    {
        if (!TryValidatePath(rawPath, out fullPath, out error))
        {
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = "La operación solo permite archivos, no carpetas.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "El archivo no existe.";
            return false;
        }

        return true;
    }

    private bool TryValidatePath(string rawPath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch
        {
            error = "La ruta no es válida.";
            return false;
        }

        if (!IsUnderAllowedRoot(fullPath))
        {
            error = "La ruta está fuera de las unidades permitidas.";
            return false;
        }

        if (IsProtected(fullPath))
        {
            error = "La ruta está protegida por el sistema.";
            return false;
        }

        if (HasReparsePoint(fullPath))
        {
            error = "La ruta contiene enlaces simbólicos o junctions no permitidos.";
            return false;
        }

        return true;
    }

    private bool IsUnderAllowedRoot(string fullPath) =>
        _roots.Any(root => IsSameOrUnder(fullPath, root));

    private bool IsProtected(string fullPath) =>
        _protectedPaths.Any(protectedPath => IsSameOrUnder(fullPath, protectedPath));

    private static bool IsSameOrUnder(string candidate, string basePathWithSeparator)
    {
        var trimmedBase = basePathWithSeparator.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidate.Equals(trimmedBase, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(basePathWithSeparator, StringComparison.OrdinalIgnoreCase)
            || (candidate + Path.DirectorySeparatorChar).Equals(basePathWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWithSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length == 3 && path[1] == ':' &&
            (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDriveRoot(string path) =>
        path.Length == 3 && path[1] == ':' &&
        (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);

    private static List<string> DetectFixedDrives()
    {
        var roots = new List<string>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    {
                        roots.Add(NormalizeWithSeparator(drive.RootDirectory.FullName));
                    }
                }
                catch
                {
                    // Unidad no accesible.
                }
            }
        }
        catch
        {
            // Sin acceso a la información de unidades.
        }

        if (roots.Count == 0)
        {
            try
            {
                roots.Add(NormalizeWithSeparator(Path.GetPathRoot(AppContext.BaseDirectory) ?? @"C:\"));
            }
            catch
            {
                roots.Add(@"C:\");
            }
        }

        return roots;
    }

    private string? GetParentWithinRoots(string fullPath)
    {
        var trimmed = TrimTrailingSeparator(fullPath);
        if (IsDriveRoot(trimmed))
        {
            return null;
        }

        var parent = Directory.GetParent(trimmed);
        if (parent == null)
        {
            return null;
        }

        var parentPath = TrimTrailingSeparator(parent.FullName);
        if (!IsUnderAllowedRoot(parentPath) || IsProtected(parentPath))
        {
            return null;
        }

        return parentPath;
    }

    private static bool HasReparsePoint(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            var relative = fullPath.Substring(root.Length);
            var current = root;

            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);

                if (Directory.Exists(current) || File.Exists(current))
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileName(fileName);

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            return null;
        }

        if (name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(baseName))
        {
            return null;
        }

        return name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Limpieza best-effort.
        }
    }

    private sealed class UploadSession
    {
        public object Sync { get; } = new();

        public string TempPath { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;

        public long ExpectedBytes { get; init; }

        public long BytesWritten { get; set; }

        public string? Error { get; set; }

        public FileStream Stream { get; init; } = null!;
    }
}
