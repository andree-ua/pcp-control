using System.ComponentModel.DataAnnotations.Schema;

namespace Pcp.Server.Models;

/// <summary>
/// Entidad de base de datos que representa una pantalla (mini-PC stick).
/// </summary>
public class Screen
{
    /// <summary>Identificador primario (ej. "screen-01").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nombre amigable de la pantalla.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Grupo al que pertenece la pantalla (opcional).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Navegación al grupo asignado.</summary>
    public ScreenGroup? Group { get; set; }

    /// <summary>Token secreto de autenticación asignado a la pantalla.</summary>
    public string Token { get; set; } = "dev-token";

    /// <summary>Estado de conexión: "online" u "offline".</summary>
    public string Status { get; set; } = "offline";

    /// <summary>Fecha y hora UTC del último latido/heartbeat recibido.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>Medio en ejecución actual ("idle" si reposo).</summary>
    public string CurrentMedia { get; set; } = "idle";

    /// <summary>Nivel de volumen (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Porcentaje de uso de CPU reportado.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Porcentaje de uso de RAM reportado.</summary>
    public double RamPercent { get; set; }

    /// <summary>Espacio libre en disco en MB.</summary>
    public long FreeDiskMb { get; set; }

    /// <summary>Último fotograma capturado en Base64 para vista previa (mantenido en memoria rápida).</summary>
    [NotMapped]
    public string? ThumbnailBase64 { get; set; }

    /// <summary>Fecha y hora UTC de creación/registro inicial de la pantalla.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
