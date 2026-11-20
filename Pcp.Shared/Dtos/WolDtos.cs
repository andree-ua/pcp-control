namespace Pcp.Shared.Dtos;

/// <summary>
/// Petición de encendido remoto (Wake-on-LAN) de una pantalla apagada.
/// </summary>
public class WakeRequestDto
{
    /// <summary>
    /// Pantalla online que debe emitir el Magic Packet. Si es null, el servidor
    /// elige automáticamente un agente de la misma red que la pantalla destino.
    /// </summary>
    public string? RelayScreenId { get; set; }
}

/// <summary>
/// Resultado del envío de un Magic Packet.
/// </summary>
public class WakeResponseDto
{
    /// <summary>Indica si el paquete se llegó a encargar a un emisor.</summary>
    public bool Requested { get; set; }

    /// <summary>"peer" si lo emite otro agente de la red; "direct" si lo envía el servidor.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Pantalla que actúa como emisora cuando <see cref="Mode"/> es "peer".</summary>
    public string? RelayScreenId { get; set; }

    /// <summary>MAC destino del Magic Packet.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Broadcast dirigido usado por el emisor (ej. "192.168.1.255").</summary>
    public string? BroadcastAddress { get; set; }

    /// <summary>Comando auditado en la tabla Commands (solo en modo "peer").</summary>
    public Guid? CommandId { get; set; }

    /// <summary>Segundos que hay que esperar antes de repetir la petición para la misma pantalla.</summary>
    public int RetryAfterSeconds { get; set; }
}

/// <summary>
/// Payload del comando "wake": instruye a un agente online a emitir el Magic Packet
/// de otra pantalla de su misma red (el servidor en la nube no puede alcanzar la LAN).
/// </summary>
public class WolPayload
{
    /// <summary>MAC de la pantalla que se quiere despertar.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Broadcast dirigido de la red destino (ej. "192.168.1.255").</summary>
    public string? BroadcastAddress { get; set; }

    /// <summary>Última IP conocida de la pantalla destino; se usa como envío adicional.</summary>
    public string? TargetIp { get; set; }

    /// <summary>Pantalla destino; solo para trazabilidad en los logs del relay.</summary>
    public string? TargetScreenId { get; set; }

    /// <summary>Número de paquetes a emitir (las tramas de broadcast pueden perderse).</summary>
    public int Repeats { get; set; } = 3;
}
