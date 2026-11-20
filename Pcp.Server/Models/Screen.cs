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

    /// <summary>Fecha y hora UTC de la última conexión del agente al Hub (no se actualiza en cada heartbeat).</summary>
    public DateTime? LastConnectionAt { get; set; }

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

    /// <summary>Dirección MAC de la interfaz física ("AA:BB:CC:DD:EE:FF"). Null si el agente aún no la reportó.</summary>
    public string? MacAddress { get; set; }

    /// <summary>Dirección IP local de la interfaz física.</summary>
    public string? LocalIp { get; set; }

    /// <summary>Máscara de subred IPv4 de la interfaz física (ej. "255.255.255.0").</summary>
    public string? SubnetMask { get; set; }

    /// <summary>Red en notación CIDR ("192.168.1.0/24"); se usa para emparejar el relay de Wake-on-LAN.</summary>
    public string? NetworkCidr { get; set; }

    /// <summary>Indica si la interfaz reportada es cableada. Null si el agente no lo informó (flota antigua).</summary>
    public bool? IsWired { get; set; }

    /// <summary>Permite al administrador excluir la pantalla del Wake-on-LAN.</summary>
    public bool WolEnabled { get; set; } = true;

    /// <summary>Último fotograma capturado en Base64 para vista previa (mantenido en memoria rápida).</summary>
    [NotMapped]
    public string? ThumbnailBase64 { get; set; }

    /// <summary>Fecha y hora UTC de creación/registro inicial de la pantalla.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
