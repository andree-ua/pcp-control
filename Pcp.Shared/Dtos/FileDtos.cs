namespace Pcp.Shared.Dtos;

/// <summary>
/// Operaciones de gestión de archivos que el servidor puede solicitar al agente.
/// </summary>
public static class FileOperations
{
    public const string List = "list";
    public const string Delete = "delete";
    public const string UploadStart = "upload-start";
    public const string UploadChunk = "upload-chunk";
    public const string UploadComplete = "upload-complete";
    public const string UploadCancel = "upload-cancel";
}

/// <summary>
/// Entrada de archivo o carpeta devuelta por el explorador remoto.
/// </summary>
public class FileEntryDto
{
    public string Name { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long SizeBytes { get; set; }

    public DateTime LastModified { get; set; }

    public string Extension { get; set; } = string.Empty;

    public bool IsVideo { get; set; }
}

/// <summary>
/// Resultado de listar una carpeta en el agente.
/// </summary>
public class FileListResultDto
{
    /// <summary>Ruta actual. Vacía cuando se listan las unidades disponibles.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Ruta de la carpeta padre, o null si ya es una raíz.</summary>
    public string? ParentPath { get; set; }

    /// <summary>Unidades/raíces permitidas para navegar.</summary>
    public List<string> Roots { get; set; } = new();

    public List<FileEntryDto> Entries { get; set; } = new();

    public bool IsRoot { get; set; }

    /// <summary>Indica si se alcanzó el límite máximo de entradas devueltas.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Solicitud de operación de archivos enviada del servidor al agente por SignalR.
/// </summary>
public class FileRequestDto
{
    public Guid RequestId { get; set; } = Guid.NewGuid();

    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Valor de <see cref="FileOperations"/>.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Ruta de carpeta o archivo según la operación.</summary>
    public string? Path { get; set; }

    /// <summary>Nombre del archivo para las operaciones de subida.</summary>
    public string? FileName { get; set; }

    /// <summary>Tamaño total esperado del archivo subido.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Índice del chunk actual (subida por bloques).</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Contenido del chunk codificado en Base64.</summary>
    public string? DataBase64 { get; set; }
}

/// <summary>
/// Nombres de los eventos SignalR de transferencia de archivos.
/// Mantener sincronizados con el panel web (src/types/files.ts).
/// </summary>
public static class FileTransferEvents
{
    /// <summary>Servidor → panel: progreso de una subida hacia el agente.</summary>
    public const string UploadProgress = "FileUploadProgress";
}

/// <summary>Fases de una subida mostradas por el panel.</summary>
public static class UploadPhases
{
    /// <summary>El servidor está enviando el archivo al agente por SignalR.</summary>
    public const string Uploading = "uploading";

    /// <summary>El agente confirmó el archivo.</summary>
    public const string Completed = "completed";

    /// <summary>La subida falló o fue cancelada.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Progreso real de una subida en el tramo servidor → agente. Se emite al grupo
/// "panel" con el identificador que generó el navegador para que cada operador
/// filtre solo su propia transferencia.
/// </summary>
public class FileUploadProgressDto
{
    /// <summary>Identificador generado por el panel para esta subida.</summary>
    public string ClientRequestId { get; set; } = string.Empty;

    public string ScreenId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>Bytes ya entregados al agente (sin contar el chunk en vuelo).</summary>
    public long SentBytes { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>Índice del último chunk enviado.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Valor de <see cref="UploadPhases"/>.</summary>
    public string Phase { get; set; } = UploadPhases.Uploading;

    /// <summary>Motivo del fallo cuando <see cref="Phase"/> es "failed".</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Respuesta del agente al servidor para una solicitud de archivos.
/// </summary>
public class FileResultDto
{
    public Guid RequestId { get; set; }

    public string ScreenId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }

    public FileListResultDto? Listing { get; set; }

    /// <summary>Ruta final del archivo guardado tras una subida correcta.</summary>
    public string? SavedPath { get; set; }
}
