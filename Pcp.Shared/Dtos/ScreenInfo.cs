namespace Pcp.Shared.Dtos;

/// <summary>
/// Representa el estado actual y métricas de una pantalla (mini-PC stick).
/// </summary>
public class ScreenInfo
{
    /// <summary>Identificador único de la pantalla (ej. "screen-01").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nombre descriptivo de la pantalla.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Grupo al que pertenece la pantalla, si tiene uno asignado.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Nombre del grupo al que pertenece la pantalla.</summary>
    public string? GroupName { get; set; }

    /// <summary>Estado de conexión: "online" u "offline".</summary>
    public string Status { get; set; } = "offline";

    /// <summary>Fecha y hora UTC del último heartbeat recibido.</summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>Ruta o identificador del medio en reproducción actual ("idle" si ninguno).</summary>
    public string CurrentMedia { get; set; } = "idle";

    /// <summary>Nivel de volumen actual (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Porcentaje estimado de uso de CPU.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Porcentaje estimado de uso de memoria RAM.</summary>
    public double RamPercent { get; set; }

    /// <summary>Espacio disponible en disco en Megabytes.</summary>
    public long FreeDiskMb { get; set; }

    /// <summary>Último fotograma/miniatura en Base64 para verificación visual.</summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>Indica si la pantalla puede abrir una sesión de control remoto (VNC).</summary>
    public bool RemoteControlAvailable { get; set; }

    /// <summary>MAC de la interfaz física; null si el agente aún no la reportó.</summary>
    public string? MacAddress { get; set; }

    /// <summary>IP local de la interfaz física.</summary>
    public string? LocalIp { get; set; }

    /// <summary>Fecha y hora UTC de la última conexión del agente al Hub.</summary>
    public DateTime? LastConnectionAt { get; set; }

    /// <summary>Indica si la pantalla acepta peticiones de Wake-on-LAN.</summary>
    public bool WolEnabled { get; set; } = true;

    /// <summary>Indica si la interfaz activa es cableada. Null si el agente no lo informó.</summary>
    public bool? IsWired { get; set; }

    /// <summary>
    /// Desfase del reloj del agente respecto al servidor en milisegundos.
    /// Null si el agente aún no sincronizó (agente antiguo o /api/time inaccesible).
    /// </summary>
    public int? ClockOffsetMs { get; set; }

    /// <summary>Indica si el agente tiene referencia de hora del servidor (sincronización lista).</summary>
    public bool ClockSynced { get; set; }
}
