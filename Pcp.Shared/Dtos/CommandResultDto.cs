namespace Pcp.Shared.Dtos;

/// <summary>
/// Notificación del resultado de la ejecución de un comando enviada por el agente al servidor.
/// </summary>
public class CommandResultDto
{
    /// <summary>Identificador del comando que fue ejecutado.</summary>
    public Guid CommandId { get; set; }

    /// <summary>Identificador de la pantalla que reporta el resultado.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Resultado: "ok" o "error".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Detalle explicativo, mensaje de confirmación o información del error.</summary>
    public string? Detail { get; set; }

    /// <summary>Fecha y hora UTC de la ejecución.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
