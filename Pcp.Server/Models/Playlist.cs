namespace Pcp.Server.Models;

/// <summary>
/// Lista ordenada de medios que se reproduce de forma continua (Bloque 4).
/// </summary>
public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Al terminar el último elemento se vuelve al primero.</summary>
    public bool Loop { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();

    /// <summary>Horarios que programan esta playlist.</summary>
    public ICollection<Schedule> Schedules { get; set; } = new List<Schedule>();
}

/// <summary>Elemento de una playlist, con su orden y duración planificada.</summary>
public class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlaylistId { get; set; }

    public Playlist? Playlist { get; set; }

    /// <summary>Orden dentro de la lista (0 = primero).</summary>
    public int Position { get; set; }

    /// <summary>Ruta local del agente o URL del medio.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Duración en segundos; 0 = hasta el fin natural del medio.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Volumen 1-100; 0 = volumen por defecto de la playlist.</summary>
    public int Volume { get; set; } = 80;
}
