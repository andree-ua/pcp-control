namespace Pcp.Shared.Dtos;

/// <summary>
/// Datos suministrados por el agente al inicializar o registrarse en el servidor.
/// </summary>
public class RegisterDto
{
    /// <summary>Identificador configurado para la pantalla (ej. "screen-01").</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Nombre asignado a la pantalla.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Token secreto de autenticación y vinculación (en Fase 0: "dev-token").</summary>
    public string Token { get; set; } = "dev-token";

    /// <summary>MAC de la interfaz física (null en agentes antiguos o si no se pudo detectar).</summary>
    public string? MacAddress { get; set; }

    /// <summary>IP local de la interfaz física.</summary>
    public string? LocalIp { get; set; }

    /// <summary>Máscara de subred IPv4 de la interfaz física.</summary>
    public string? SubnetMask { get; set; }

    /// <summary>Red en notación CIDR ("192.168.1.0/24") para emparejar el relay de Wake-on-LAN.</summary>
    public string? NetworkCidr { get; set; }

    /// <summary>Indica si la interfaz activa es cableada (Ethernet) o inalámbrica (Wi-Fi).</summary>
    public bool? IsWired { get; set; }
}
