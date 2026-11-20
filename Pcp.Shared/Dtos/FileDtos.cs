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
