namespace Pcp.Shared.Dtos;

/// <summary>
/// Representa una orden o comando emitido por el servidor hacia un agente.
/// </summary>
public class CommandDto
{
    /// <summary>Identificador único del comando para seguimiento.</summary>
    public Guid CommandId { get; set; } = Guid.NewGuid();

    /// <summary>Identificador de la pantalla de destino.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Tipo de acción requerida (ej. "play", "pause", "stop", "volume", "shutdown", "restart").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Parámetros adicionales serializados en formato JSON según el tipo.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Fecha y hora UTC de generación del comando.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
