namespace Pcp.Shared.Dtos;

/// <summary>
/// Elemento de una playlist: un medio y su duración planificada.
/// </summary>
public class PlaylistItemDto
{
    public Guid Id { get; set; }

    /// <summary>Orden dentro de la playlist (0 = primero).</summary>
    public int Position { get; set; }

    /// <summary>Ruta local del agente o URL del medio.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>
    /// Duración planificada en segundos. 0 = hasta que el propio medio termine.
    /// Con un valor mayor se corta el elemento y se pasa al siguiente.
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>Volumen del elemento (1-100). 0 = usar el volumen por defecto de la playlist.</summary>
    public int Volume { get; set; } = 80;
}

/// <summary>Playlist completa tal como la consume el panel y el agente.</summary>
public class PlaylistDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Repetir la playlist en bucle al terminar el último elemento.</summary>
    public bool Loop { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<PlaylistItemDto> Items { get; set; } = new();
}

/// <summary>Entrada de un elemento al crear/editar una playlist.</summary>
public class PlaylistItemInputDto
{
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Duración en segundos (0 = fin natural del medio).</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Volumen 1-100; 0 usa el volumen por defecto de la playlist.</summary>
    public int Volume { get; set; } = 80;
}

/// <summary>Creación/edición de una playlist: reemplaza por completo sus elementos.</summary>
public class PlaylistUpsertDto
{
    public string Name { get; set; } = string.Empty;

    public bool Loop { get; set; } = true;

    public List<PlaylistItemInputDto> Items { get; set; } = new();
}

/// <summary>Petición del panel para reproducir una playlist ahora mismo.</summary>
public class PlaylistPlayRequestDto
{
    /// <summary>Pantallas destino; se ignora si se indica <see cref="GroupId"/>.</summary>
    public List<string>? ScreenIds { get; set; }

    /// <summary>Grupo destino (todas sus pantallas).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Margen de seguridad en segundos antes de arrancar el primer elemento (3-120).</summary>
    public int StartInSeconds { get; set; } = 8;
}

/// <summary>Resultado por pantalla del lanzamiento de una playlist.</summary>
public class PlaylistDispatchItemDto
{
    public string ScreenId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? Error { get; set; }
}

/// <summary>Resumen del lanzamiento de una playlist.</summary>
public class PlaylistPlayResponseDto
{
    public Guid PlaylistId { get; set; }

    public string Name { get; set; } = string.Empty;

    public long StartAtUnixMs { get; set; }

    public DateTime StartAtUtc { get; set; }

    public int Total { get; set; }

    public int Sent { get; set; }

    public int Offline { get; set; }

    public int Failed { get; set; }

    public List<PlaylistDispatchItemDto> Results { get; set; } = new();
}

/// <summary>
/// Payload del comando "play-playlist": el agente reproduce los elementos en
/// orden de forma autónoma (sin que el servidor tenga que avanzar cada uno).
/// </summary>
public class PlaylistCommandPayload
{
    public Guid PlaylistId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Loop { get; set; } = true;

    /// <summary>Volumen por defecto para los elementos con Volume = 0.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>
    /// Instante de arranque en el reloj del servidor (epoch ms). Null = inmediato.
    /// Se usa para que un grupo arranque la playlist a la vez (Bloque 3).
    /// </summary>
    public long? StartAtUnixMs { get; set; }

    public List<PlaylistItemDto> Items { get; set; } = new();
}

// ── Programación (horarios) ───────────────────────────────────────────────

/// <summary>Horario recurrente que lanza una playlist en una pantalla o grupo.</summary>
public class ScheduleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid PlaylistId { get; set; }

    public string PlaylistName { get; set; } = string.Empty;

    public string? ScreenId { get; set; }

    public string? ScreenName { get; set; }

    public Guid? GroupId { get; set; }

    public string? GroupName { get; set; }

    /// <summary>Días ISO separados por coma: 1 = lunes … 7 = domingo.</summary>
    public string DaysOfWeek { get; set; } = "1,2,3,4,5";

    /// <summary>Hora local de inicio "HH:mm" en la zona <see cref="TimeZone"/>.</summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>Duración de la ventana en minutos; 0 = se calcula con la duración de la playlist.</summary>
    public int DurationMinutes { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>Zona horaria con la que se interpreta <see cref="StartTime"/>.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Próxima ejecución calculada (UTC). Null si está deshabilitado.</summary>
    public DateTime? NextRunAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Creación/edición de un horario.</summary>
public class ScheduleUpsertDto
{
    /// <summary>Nombre visible; si se omite se genera con el nombre de la playlist y la hora.</summary>
    public string? Name { get; set; }

    public Guid PlaylistId { get; set; }

    /// <summary>Pantalla destino. Debe indicarse exactamente uno de ScreenId o GroupId.</summary>
    public string? ScreenId { get; set; }

    /// <summary>Grupo destino. Debe indicarse exactamente uno de ScreenId o GroupId.</summary>
    public Guid? GroupId { get; set; }

    public List<int> DaysOfWeek { get; set; } = new() { 1, 2, 3, 4, 5, 6, 7 };

    public string StartTime { get; set; } = "08:00";

    public int DurationMinutes { get; set; }

    public bool Enabled { get; set; } = true;
}
