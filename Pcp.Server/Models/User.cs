namespace Pcp.Server.Models;

/// <summary>
/// Usuario del panel de control. La contraseña se guarda como hash PBKDF2, nunca en claro.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nombre de usuario único (se normaliza a minúsculas al comparar).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Nombre visible en el panel.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Hash con formato pbkdf2-sha256$iteraciones$salt$hash.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Rol del usuario: "Admin" o "Viewer".</summary>
    public string Role { get; set; } = "Viewer";

    /// <summary>Permite deshabilitar una cuenta sin borrarla.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }
}
