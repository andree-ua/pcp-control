using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Server.Services;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// Autenticación del panel y emisión de tokens para los agentes.
/// El panel recibe el JWT en una cookie HttpOnly; el agente lo recibe en el cuerpo
/// de la respuesta porque no tiene navegador ni almacén de cookies.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // Hash válido generado una sola vez: mantiene el coste de verificación cuando el
    // usuario no existe y evita filtrar qué cuentas están registradas por tiempo.
    private static readonly string DummyPasswordHash = PasswordHasher.Hash("pcp-dummy-password");

    private readonly AppDbContext _db;
    private readonly TokenService _tokens;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        TokenService tokens,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AuthController> logger)
    {
        _db = db;
        _tokens = tokens;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/login
    /// Valida las credenciales, actualiza el último acceso y deja el JWT en una cookie HttpOnly.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var username = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        var password = request.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return BadRequest(new { error = "Usuario y contraseña son obligatorios." });
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username, cancellationToken);

        // Verificación con hash ficticio cuando el usuario no existe: evita distinguir
        // por tiempo de respuesta si la cuenta está registrada.
        var passwordOk = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyPasswordHash);

        if (user == null || !passwordOk || !user.IsActive)
        {
            _logger.LogWarning("Intento de login fallido para '{Username}' desde {Ip}",
                username, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Credenciales inválidas." });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var (token, expiresAt) = _tokens.CreateUserToken(user);
        WriteAuthCookie(token, expiresAt);

        _logger.LogInformation("Login correcto: '{Username}' ({Role})", user.Username, user.Role);

        // El JWT no se devuelve al navegador: viaja solo en la cookie HttpOnly.
        return Ok(new LoginResponse
        {
            Token = string.Empty,
            ExpiresAtUtc = expiresAt,
            User = ToDto(user)
        });
    }

    /// <summary>
    /// POST /api/auth/logout
    /// Elimina la cookie de sesión. Es idempotente y no exige estar autenticado.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Path = "/",
            Secure = UseSecureCookie(),
            SameSite = ResolveSameSite()
        });

        return NoContent();
    }

    /// <summary>
    /// GET /api/auth/me
    /// Devuelve el usuario de la sesión actual. El panel lo usa al arrancar.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var subject = User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(subject, out var userId))
        {
            return Unauthorized(new { error = "Sesión no válida." });
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);

        if (user == null)
        {
            return Unauthorized(new { error = "La cuenta ya no está disponible." });
        }

        return Ok(ToDto(user));
    }

    /// <summary>
    /// POST /api/auth/agent-token
    /// Canjea el token de dispositivo del agente por un JWT con rol Agent y claim screen_id.
    /// </summary>
    [HttpPost("agent-token")]
    [AllowAnonymous]
    public async Task<IActionResult> AgentToken([FromBody] AgentTokenRequest request, CancellationToken cancellationToken)
    {
        var screenId = (request.ScreenId ?? string.Empty).Trim();
        var deviceToken = request.DeviceToken ?? string.Empty;

        if (string.IsNullOrWhiteSpace(screenId) || string.IsNullOrEmpty(deviceToken))
        {
            return BadRequest(new { error = "ScreenId y DeviceToken son obligatorios." });
        }

        var screen = await _db.Screens
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == screenId, cancellationToken);

        if (screen == null || !TokensEqual(screen.Token, deviceToken))
        {
            _logger.LogWarning("Token de agente rechazado para pantalla '{ScreenId}' desde {Ip}",
                screenId, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Token de dispositivo inválido." });
        }

        if (!_environment.IsDevelopment() &&
            string.Equals(screen.Token, "dev-token", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "La pantalla '{ScreenId}' usa el token por defecto 'dev-token' en producción. " +
                "Rote el token del agente antes de exponer la flota.", screenId);
        }

        var (token, expiresAt) = _tokens.CreateAgentToken(screenId);

        _logger.LogInformation("Token de agente emitido para '{ScreenId}' (expira {ExpiresAt:u})",
            screenId, expiresAt);

        return Ok(new AgentTokenResponse
        {
            Token = token,
            ExpiresAtUtc = expiresAt,
            ScreenId = screenId
        });
    }

    private void WriteAuthCookie(string token, DateTime expiresAtUtc)
    {
        Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = UseSecureCookie(),
            SameSite = ResolveSameSite(),
            Path = "/",
            Expires = expiresAtUtc,
            IsEssential = true
        });
    }

    private string CookieName => _configuration["Auth:CookieName"] ?? "pcp_auth";

    private bool UseSecureCookie()
    {
        var configured = _configuration.GetValue<bool?>("Auth:CookieSecure");
        return configured ?? Request.IsHttps;
    }

    private SameSiteMode ResolveSameSite()
    {
        var configured = _configuration["Auth:CookieSameSite"];
        if (!string.IsNullOrWhiteSpace(configured) &&
            Enum.TryParse<SameSiteMode>(configured, ignoreCase: true, out var mode))
        {
            return mode;
        }

        return _environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None;
    }

    private static bool TokensEqual(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        var providedBytes = Encoding.UTF8.GetBytes(provided ?? string.Empty);

        if (expectedBytes.Length != providedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static AuthUserDto ToDto(User user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        DisplayName = user.DisplayName,
        Role = user.Role
    };
}
