namespace Pcp.Server.Models;

/// <summary>
/// Grupo lógico de pantallas para operaciones de control masivo.
/// </summary>
public class ScreenGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Screen> Screens { get; set; } = new List<Screen>();
}
