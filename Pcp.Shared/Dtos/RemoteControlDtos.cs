namespace Pcp.Shared.Dtos;

/// <summary>
/// Nombres exactos de los eventos/métodos SignalR usados por el control remoto.
/// El agente y el servidor dependen de ellos: no renombrar sin actualizar ambos lados.
/// </summary>
public static class RemoteControlEvents
{
    /// <summary>Servidor → agente: abre el túnel VNC de una sesión.</summary>
    public const string OpenTunnel = "OpenVncTunnel";

    /// <summary>Servidor → agente: cierra el túnel VNC de una sesión.</summary>
    public const string CloseTunnel = "CloseVncTunnel";

    /// <summary>Agente → servidor: informa el resultado de apertura del túnel.</summary>
    public const string TunnelStatus = "VncTunnelStatus";
}

/// <summary>
/// Orden enviada por el servidor al agente para preparar el escritorio remoto.
/// </summary>
public class VncTunnelRequestDto
{
    /// <summary>Identificador único y efímero de la sesión de control.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla que debe abrir el túnel.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>
    /// Ruta relativa (con query) del túnel en el servidor, por ejemplo
    /// "/ws/vnc-agent/{sessionId}?secret=...". El agente le antepone su propio ServerUrl.
    /// </summary>
    public string TunnelPath { get; set; } = string.Empty;

    /// <summary>Contraseña RFB efímera (máximo 8 caracteres) que debe aplicar el servidor VNC local.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Puerto local solicitado (0 = elegir uno libre automáticamente).</summary>
    public int PreferredPort { get; set; }

    /// <summary>Si es true, el agente no debe aceptar entrada (solo visualización).</summary>
    public bool ViewOnly { get; set; }
}

/// <summary>
/// Confirmación del agente con el estado del túnel VNC.
/// </summary>
public class VncTunnelStatusDto
{
    /// <summary>Sesión a la que corresponde el estado.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla que abrió el túnel.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Indica si el servidor VNC y el túnel quedaron operativos.</summary>
    public bool Success { get; set; }

    /// <summary>Detalle o mensaje de error para el operador.</summary>
    public string? Detail { get; set; }

    /// <summary>Puerto RFB local realmente usado (solo loopback).</summary>
    public int Port { get; set; }
}

/// <summary>
/// Respuesta REST con los datos que el panel necesita para abrir la sesión noVNC.
/// </summary>
public class RemoteSessionResponse
{
    /// <summary>Sesión efímera creada por el servidor.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla controlada.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>URL WebSocket a la que se debe conectar el cliente noVNC.</summary>
    public string WebSocketUrl { get; set; } = string.Empty;

    /// <summary>Contraseña RFB efímera (se envía porque el RFB la exige en el handshake).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Segundos de validez para que el panel se conecte.</summary>
    public int ExpiresInSeconds { get; set; }

    /// <summary>Modo solo lectura (sin ratón ni teclado).</summary>
    public bool ViewOnly { get; set; }
}
