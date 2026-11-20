using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Obtiene y cachea el JWT del agente canjeando su token de dispositivo contra
/// <c>POST /api/auth/agent-token</c>. Se renueva poco antes de caducar.
/// </summary>
public class AgentTokenProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentTokenProvider> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public AgentTokenProvider(IConfiguration configuration, ILogger<AgentTokenProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Devuelve un token válido. Si no hay token o está por caducar, lo solicita al servidor.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
            {
                return _token;
            }

            var screenId = _configuration["Agent:ScreenId"] ?? "screen-01";
            var deviceToken = _configuration["Agent:Token"] ?? "dev-token";
            var serverUrl = (_configuration["Agent:ServerUrl"] ?? "http://localhost:5000").TrimEnd('/');

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/auth/agent-token")
            {
                Content = JsonContent.Create(new AgentTokenRequest
                {
                    ScreenId = screenId,
                    DeviceToken = deviceToken
                })
            };

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "No se pudo obtener el token JWT del agente (HTTP {Status}): {Body}",
                    (int)response.StatusCode, body);
                _token = null;
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<AgentTokenResponse>(cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Token))
            {
                _logger.LogWarning("El servidor devolvió una respuesta de token vacía.");
                _token = null;
                return null;
            }

            _token = payload.Token;
            _expiresAtUtc = payload.ExpiresAtUtc;
            _logger.LogInformation("Token JWT de agente obtenido; expira {ExpiresAt:u}.", _expiresAtUtc);
            return _token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error solicitando el token JWT del agente: {Message}", ex.Message);
            _token = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Descarta el token cacheado para forzar su renovación en el próximo uso.</summary>
    public void Invalidate()
    {
        _token = null;
        _expiresAtUtc = DateTime.MinValue;
    }
}
