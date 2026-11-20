namespace Pcp.Shared.Dtos;

/// <summary>
/// Hora oficial del servidor (GET /api/time). El agente la usa para calcular el
/// desfase de su reloj local y así poder arrancar un vídeo en el mismo instante
/// que el resto de la flota (Bloque 3: sincronización).
/// </summary>
public class ServerTimeDto
{
    /// <summary>Hora UTC del servidor en milisegundos desde la época Unix.</summary>
    public long UnixTimeMs { get; set; }

    /// <summary>Hora UTC del servidor como fecha legible.</summary>
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;

    /// <summary>Identificador del servicio que responde; útil en diagnósticos.</summary>
    public string Server { get; set; } = "pcp-server";
}

/// <summary>
/// Payload del comando "sync-play": ordena al agente arrancar un medio en un
/// instante absoluto del reloj del servidor.
/// </summary>
public class SyncPlayPayload
{
    /// <summary>Ruta del archivo local o URL remota del medio.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Indica si el medio debe repetirse en bucle.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Volumen inicial (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Modo de encuadre solicitado ("fit", "cover", "stretch").</summary>
    public string FitMode { get; set; } = "cover";

    /// <summary>
    /// Instante de arranque en milisegundos desde la época Unix, expresado en el
    /// reloj del servidor. Los agentes lo convierten a su reloj local con el
    /// offset calculado contra /api/time.
    /// </summary>
    public long StartAtUnixMs { get; set; }

    /// <summary>Identificador de la orden; agrupa el mismo arranque en varias pantallas.</summary>
    public string? SyncToken { get; set; }
}

/// <summary>
/// Petición del panel para reproducir el mismo medio a la vez en varias pantallas
/// (POST /api/screens/sync/play).
/// </summary>
public class SyncPlayRequestDto
{
    /// <summary>Pantallas destino. Se ignora si se indica <see cref="GroupId"/>.</summary>
    public List<string>? ScreenIds { get; set; }

    /// <summary>Grupo destino: se usan todas sus pantallas asignadas.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Ruta del archivo local o URL remota del medio.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Repetir en bucle.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Volumen inicial (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Modo de encuadre solicitado.</summary>
    public string FitMode { get; set; } = "cover";

    /// <summary>
    /// Margen de seguridad en segundos entre la petición y el arranque. Da tiempo
    /// a que todos los agentes reciban el comando. Se limita a 3-120 s.
    /// </summary>
    public int StartInSeconds { get; set; } = 8;
}

/// <summary>Resultado del envío de la orden sincronizada a una pantalla.</summary>
public class SyncPlayResultItemDto
{
    public string ScreenId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? Error { get; set; }
}

/// <summary>Resumen del envío de una reproducción sincronizada.</summary>
public class SyncPlayResponseDto
{
    /// <summary>Identificador común de la orden (auditoría y diagnóstico).</summary>
    public string SyncToken { get; set; } = string.Empty;

    /// <summary>Instante de arranque en el reloj del servidor (epoch Unix, ms).</summary>
    public long StartAtUnixMs { get; set; }

    /// <summary>Mismo instante como fecha UTC legible.</summary>
    public DateTime StartAtUtc { get; set; }

    /// <summary>Margen realmente aplicado (segundos).</summary>
    public int StartInSeconds { get; set; }

    public int Total { get; set; }

    public int Sent { get; set; }

    public int Offline { get; set; }

    public int Failed { get; set; }

    public List<SyncPlayResultItemDto> Results { get; set; } = new();
}
