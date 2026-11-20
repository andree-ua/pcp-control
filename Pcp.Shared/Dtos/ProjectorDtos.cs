namespace Pcp.Shared.Dtos;

/// <summary>
/// Nombres exactos de los eventos/métodos SignalR usados por el proyector (Bloque 5).
/// El servidor, el agente y el panel dependen de ellos: no renombrar sin actualizar los tres lados.
/// </summary>
public static class ProjectorEvents
{
    /// <summary>Servidor → agente: empieza a reproducir la emisión de la sesión.</summary>
    public const string Open = "OpenProjector";

    /// <summary>Servidor → agente: detiene la emisión de la sesión.</summary>
    public const string Close = "CloseProjector";

    /// <summary>Agente → servidor: resultado de abrir o cerrar la emisión.</summary>
    public const string Status = "ProjectorStatus";

    /// <summary>Servidor → panel: retransmisión del estado reportado por el agente.</summary>
    public const string StatusReceived = "ProjectorStatusReceived";
}

/// <summary>
/// Orden enviada por el servidor al agente para reproducir el stream publicado
/// desde el panel a través de MediaMTX.
/// </summary>
public class ProjectorCommandPayload
{
    /// <summary>Identificador efímero de la sesión de proyección.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla que debe mostrar la emisión.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>
    /// URL principal del stream (RTSP con credenciales de lectura). El agente
    /// la reproduce con caché bajo para minimizar el retardo.
    /// </summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL alternativa (HLS) que el agente usa si el RTSP no arranca tras varios
    /// intentos. Puede ser null si no está configurada.
    /// </summary>
    public string? FallbackUrl { get; set; }

    /// <summary>Protocolo principal ("rtsp", "rtsps", "http", "https").</summary>
    public string Protocol { get; set; } = "rtsp";

    /// <summary>Volumen local de reproducción (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Caché de red en milisegundos (bajo = menos latencia).</summary>
    public int BufferMs { get; set; } = 400;

    /// <summary>Etiqueta mostrada en la telemetría y en las miniaturas.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// Estado que el agente reporta al servidor tras abrir o cerrar la emisión.
/// </summary>
public class ProjectorStatusDto
{
    /// <summary>Sesión a la que corresponde el estado.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla que reproduce la emisión.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Indica si el reproductor aceptó la orden (no implica que el vídeo ya esté visible).</summary>
    public bool Success { get; set; }

    /// <summary>Detalle o motivo de error para el operador.</summary>
    public string? Detail { get; set; }

    /// <summary>Protocolo efectivo usado para reproducir ("rtsp", "hls", ...).</summary>
    public string? Protocol { get; set; }
}

/// <summary>
/// Respuesta REST con todo lo que necesita el panel para publicar la pantalla
/// por WHIP y lo que el agente usará para leer el stream.
/// </summary>
public class ProjectorSessionResponse
{
    /// <summary>Sesión efímera creada por el servidor.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla que proyectará la emisión.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Ruta del stream en MediaMTX (por ejemplo "pcp/screen-01").</summary>
    public string StreamPath { get; set; } = string.Empty;

    /// <summary>URL WHIP a la que el navegador publica el MediaStream (incluye el token de publicación).</summary>
    public string WhipUrl { get; set; } = string.Empty;

    /// <summary>
    /// Token de publicación. El panel lo envía como <c>Authorization: Bearer</c>
    /// en el POST WHIP (método documentado por MediaMTX para WebRTC).
    /// </summary>
    public string WhipToken { get; set; } = string.Empty;

    /// <summary>URL RTSP de lectura con credenciales (la usa el agente).</summary>
    public string RtspUrl { get; set; } = string.Empty;

    /// <summary>URL HLS de lectura con credenciales (respaldo del agente y diagnóstico).</summary>
    public string HlsUrl { get; set; } = string.Empty;

    /// <summary>Caché de red solicitada al agente, en milisegundos.</summary>
    public int BufferMs { get; set; }

    /// <summary>Volumen solicitado al agente (0-100).</summary>
    public int Volume { get; set; }

    /// <summary>Segundos de validez para empezar a publicar antes de que la sesión caduque.</summary>
    public int ExpiresInSeconds { get; set; }
}

/// <summary>
/// Respuesta de los endpoints que ordenan algo al agente (empezar o parar).
/// </summary>
public class ProjectorActionResponse
{
    /// <summary>Sesión afectada.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Pantalla afectada.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>"live", "closed" o "error".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Detalle devuelto por el agente o por el servidor.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Estado actual de la sesión de proyección de una pantalla.
/// </summary>
public class ProjectorStatusResponse
{
    /// <summary>True si hay una sesión activa (publicando o preparada).</summary>
    public bool Active { get; set; }

    /// <summary>Sesión activa, si existe.</summary>
    public string? SessionId { get; set; }

    /// <summary>Pantalla consultada.</summary>
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Ruta del stream en MediaMTX.</summary>
    public string? StreamPath { get; set; }

    /// <summary>True cuando el panel ya confirmó que está publicando.</summary>
    public bool IsLive { get; set; }

    /// <summary>Último detalle reportado por el agente.</summary>
    public string? Detail { get; set; }

    /// <summary>Instante UTC en que caduca la sesión.</summary>
    public DateTime? ExpiresAt { get; set; }
}
