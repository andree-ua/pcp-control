using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Hubs;

namespace Pcp.Server.Services;

/// <summary>
/// Tarea en segundo plano encargada de auditar la actividad de las pantallas.
/// Si una pantalla online supera los 30 segundos sin emitir latido (heartbeat), se marca automáticamente como offline.
/// </summary>
public class OfflineCheckerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<ScreenHub> _hubContext;
    private readonly ILogger<OfflineCheckerService> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(30);

    public OfflineCheckerService(
        IServiceProvider serviceProvider,
        IHubContext<ScreenHub> hubContext,
        ILogger<OfflineCheckerService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Servicio OfflineCheckerService activo (Intervalo: {Interval}s, Timeout: {Timeout}s)",
            CheckInterval.TotalSeconds, OfflineTimeout.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await EvaluateScreensTimeoutAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error durante la comprobación de pantallas offline");
            }
        }
    }

    private async Task EvaluateScreensTimeoutAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoffTime = DateTime.UtcNow - OfflineTimeout;

        var screensToGoOffline = await db.Screens
            .Where(s => s.Status == "online" && s.LastHeartbeat < cutoffTime)
            .ToListAsync();

        if (screensToGoOffline.Count > 0)
        {
            foreach (var screen in screensToGoOffline)
            {
                screen.Status = "offline";
                _logger.LogWarning("Pantalla '{ScreenId}' marcada OFFLINE por inactividad (>30s sin heartbeat)", screen.Id);

                // Notificar en tiempo real a los observadores del panel
                await _hubContext.Clients.Group("panel").SendAsync("ScreenStatusChanged", screen.Id, "offline");
            }

            await db.SaveChangesAsync();
        }
    }
}
