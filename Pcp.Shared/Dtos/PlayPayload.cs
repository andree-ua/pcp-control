namespace Pcp.Shared.Dtos;

/// <summary>
/// Parámetros específicos para la instrucción de reproducción "play".
/// </summary>
public class PlayPayload
{
    /// <summary>Ruta del archivo local o URL remota del medio a reproducir.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Indica si el medio debe repetirse en bucle.</summary>
    public bool Loop { get; set; } = false;

    /// <summary>Nivel de volumen inicial de reproducción (0-100).</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Modo de escalado y encuadre en pantalla ("fit", "cover", "stretch").</summary>
    public string FitMode { get; set; } = "fit";
}
