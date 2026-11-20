namespace Pcp.Shared.Dtos;

/// <summary>
/// DTO para la transferencia del fotograma/miniatura en tiempo real desde el agente hacia el servidor.
/// </summary>
public class ThumbnailDto
{
    /// <summary>Identificador de la pantalla emisora.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Imagen JPEG codificada en Base64 (con o sin prefijo data:image/jpeg;base64,).</summary>
    public string ThumbnailBase64 { get; set; } = string.Empty;

    /// <summary>Marca de tiempo UTC de la captura.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
