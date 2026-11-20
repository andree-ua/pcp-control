using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pcp.Server.Data;

namespace Pcp.Server.Services;

/// <summary>
/// Autenticación temporal para agentes de la Fase 1/2 que todavía se conectan al Hub
/// con <c>?screenId=...&amp;token=...</c> en lugar de un JWT.
///
/// Solo se activa cuando <c>Auth:AllowLegacyAgentTokens</c> es true. Permite actualizar
/// el servidor sin dejar la flota desconectada y se desactiva cuando todos los agentes
/// ya usan <c>/api/auth/agent-token</c>.
/// </summary>
public class LegacyAgentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "LegacyAgent";

    private readonly IServiceScopeFactory _scopeFactory;

    public LegacyAgentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory)
        : base(options, logger, encoder)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuration = Context.RequestServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue("Auth:AllowLegacyAgentTokens", true))
        {
            return AuthenticateResult.NoResult();
        }

        var screenId = Request.Query["screenId"].ToString();
        var providedToken = Request.Query["token"].ToString();

        if (string.IsNullOrWhiteSpace(screenId) || string.IsNullOrEmpty(providedToken))
        {
            return AuthenticateResult.NoResult();
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var screen = await db.Screens
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == screenId);

        if (screen == null || !TokensEqual(screen.Token, providedToken))
        {
            Logger.LogWarning("Token legacy rechazado para pantalla '{ScreenId}'.", screenId);
            return AuthenticateResult.Fail("Token de agente inválido.");
        }

        Logger.LogWarning(
            "El agente '{ScreenId}' sigue usando autenticación legacy por query string. " +
            "Actualice el agente y desactive Auth:AllowLegacyAgentTokens.", screenId);

        var claims = new[]
        {
            new Claim("sub", screenId),
            new Claim("name", screenId),
            new Claim("role", TokenService.AgentRole),
            new Claim(TokenService.ScreenIdClaim, screenId)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static bool TokensEqual(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        var providedBytes = Encoding.UTF8.GetBytes(provided ?? string.Empty);

        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
