namespace Pcp.Shared.Dtos;

/// <summary>
/// Credenciales enviadas por el panel para iniciar sesión.
/// </summary>
public class LoginRequest
{
    /// <summary>Nombre de usuario.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Contraseña en claro (solo viaja por HTTPS).</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Datos públicos del usuario autenticado (nunca incluye el hash de contraseña).
/// </summary>
public class AuthUserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// Respuesta del inicio de sesión. Además del token se devuelve el usuario y la caducidad
/// para que el panel pueda mostrar el estado de la sesión sin decodificar el JWT.
/// </summary>
public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public AuthUserDto User { get; set; } = new();
}

/// <summary>
/// Canje del token de dispositivo de un agente por un JWT de corta duración.
/// </summary>
public class AgentTokenRequest
{
    public string ScreenId { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
}

/// <summary>
/// JWT emitido al agente. El agente lo usa en SignalR y en las llamadas REST protegidas.
/// </summary>
public class AgentTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string ScreenId { get; set; } = string.Empty;
}
