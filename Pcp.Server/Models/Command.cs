namespace Pcp.Server.Models;

/// <summary>
/// Entidad de base de datos que representa una orden enviada a una pantalla.
/// </summary>
public class Command
{
    /// <summary>Identificador primario único del comando.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identificador de la pantalla destino.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Tipo de instrucción ("play", "pause", "stop", "volume", etc.).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Carga útil serializada en formato JSON.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Estado del ciclo de vida: "pending", "sent", "ok", "error".</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Fecha y hora UTC de creación del comando.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Fecha y hora UTC en la que el agente reportó la ejecución.</summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>Detalles informativos o mensaje de error retornado por el agente.</summary>
    public string? ResultDetail { get; set; }
}
