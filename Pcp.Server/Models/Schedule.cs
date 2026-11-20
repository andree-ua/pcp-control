namespace Pcp.Server.Models;

/// <summary>
/// Horario recurrente que lanza una playlist en una pantalla o en un grupo
/// (Bloque 4). La hora se interpreta en la zona configurada en
/// <c>Scheduling:TimeZone</c> y el servidor calcula el instante absoluto.
/// </summary>
public class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid PlaylistId { get; set; }

    public Playlist? Playlist { get; set; }

    /// <summary>Pantalla destino (excluyente con <see cref="GroupId"/>).</summary>
    public string? ScreenId { get; set; }

    public Screen? Screen { get; set; }

    /// <summary>Grupo destino (excluyente con <see cref="ScreenId"/>).</summary>
    public Guid? GroupId { get; set; }

    public ScreenGroup? Group { get; set; }

    /// <summary>Días ISO separados por coma: 1 = lunes … 7 = domingo.</summary>
    public string DaysOfWeek { get; set; } = "1,2,3,4,5";

    /// <summary>Hora local de inicio "HH:mm".</summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>Duración de la ventana en minutos; 0 = duración de la playlist (mínimo 30 min para reintentos).</summary>
    public int DurationMinutes { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Clave del último hueco ejecutado ("yyyyMMdd-HH:mm"). Evita relanzar el
    /// mismo horario en cada tick y sobrevive a reinicios del servidor.
    /// </summary>
    public string? LastTriggeredSlot { get; set; }

    public DateTime? LastTriggeredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
