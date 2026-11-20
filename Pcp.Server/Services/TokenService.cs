using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Pcp.Server.Models;

namespace Pcp.Server.Services;

/// <summary>
/// Emisión de los dos tipos de JWT del sistema: usuarios del panel y agentes.
/// Los tokens incluyen el rol y, en el caso de los agentes, el ScreenId como claim.
/// </summary>
public class TokenService
{
    public const string AgentRole = "Agent";
    public const string AdminRole = "Admin";
    public const string ViewerRole = "Viewer";
    public const string ScreenIdClaim = "screen_id";

    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _signingKey;
    private readonly int _userTokenMinutes;
    private readonly int _agentTokenDays;

    public TokenService(IConfiguration configuration, IHostEnvironment environment)
    {
        _issuer = configuration["Auth:Issuer"] ?? "pcp";
        _audience = configuration["Auth:Audience"] ?? "pcp-panel";
        _signingKey = ResolveSigningKey(configuration, environment.IsProduction());
        _userTokenMinutes = Math.Clamp(configuration.GetValue("Auth:UserTokenMinutes", 480), 5, 10_080);
        _agentTokenDays = Math.Clamp(configuration.GetValue("Auth:AgentTokenDays", 30), 1, 365);
    }

    public (string Token, DateTime ExpiresAtUtc) CreateUserToken(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_userTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("name", user.Username),
            new("display_name", user.DisplayName),
            new("role", user.Role)
        };

        return (WriteToken(claims, expiresAt), expiresAt);
    }

    public (string Token, DateTime ExpiresAtUtc) CreateAgentToken(string screenId)
    {
        var expiresAt = DateTime.UtcNow.AddDays(_agentTokenDays);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, screenId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("name", screenId),
            new("role", AgentRole),
            new(ScreenIdClaim, screenId)
        };

        return (WriteToken(claims, expiresAt), expiresAt);
    }

    private string WriteToken(IEnumerable<Claim> claims, DateTime expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Un único punto para resolver la clave de firma. En producción es obligatoria
    /// (mínimo 32 caracteres); en desarrollo se usa una clave estable solo para pruebas.
    /// </summary>
    public static string ResolveSigningKey(IConfiguration configuration, bool isProduction)
    {
        var configured = configuration["Auth:SigningKey"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    "Falta Auth__SigningKey. Configure una clave de al menos 32 caracteres para firmar los JWT.");
            }

            return "pcp-dev-signing-key-change-me-32-chars!!";
        }

        if (configured.Length < 32)
        {
            throw new InvalidOperationException(
                "Auth:SigningKey debe tener al menos 32 caracteres para firmar los JWT de forma segura.");
        }

        return configured;
    }

    public static string ResolveIssuer(IConfiguration configuration)
        => configuration["Auth:Issuer"] ?? "pcp";

    public static string ResolveAudience(IConfiguration configuration)
        => configuration["Auth:Audience"] ?? "pcp-panel";
}
