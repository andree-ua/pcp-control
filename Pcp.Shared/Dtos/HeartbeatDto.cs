namespace Pcp.Shared.Dtos;

/// <summary>
/// Paquete de telemetría y estado periódico enviado por el agente cada 10 segundos.
/// </summary>
public class HeartbeatDto
{
    /// <summary>Identificador de la pantalla que emite el latido.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Marca de tiempo UTC del envío.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Medio en reproducción actual ("idle" si está en reposo).</summary>
    public string CurrentMedia { get; set; } = "idle";

    /// <summary>Nivel de volumen actual (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Uso de CPU medido o estimado.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Uso de RAM medido o estimado.</summary>
    public double RamPercent { get; set; }

    /// <summary>Espacio libre en disco principal en MB.</summary>
    public long FreeDiskMb { get; set; }

    /// <summary>Indica si la salida de video/HDMI está activa.</summary>
    public bool HdmiOk { get; set; } = true;

    /// <summary>Indica si el agente tiene un servidor VNC disponible para control remoto.</summary>
    public bool RemoteControlAvailable { get; set; }
}
