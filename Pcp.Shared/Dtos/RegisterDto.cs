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
}
