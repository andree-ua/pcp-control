using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Pcp.Server.Data;
using Pcp.Server.Hubs;
using Pcp.Server.Models;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Lógica compartida para registrar y enviar comandos a una pantalla.
/// La usan tanto el control individual como el control masivo por grupo.
/// </summary>
public class ScreenCommandService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ScreenHub> _hubContext;
    private readonly IScreenConnectionManager _connectionManager;
    private readonly ILogger<ScreenCommandService> _logger;

    public ScreenCommandService(
        AppDbContext db,
        IHubContext<ScreenHub> hubContext,
        IScreenConnectionManager connectionManager,
        ILogger<ScreenCommandService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<CommandDispatchResult> SendAsync(
        string screenId,
        string type,
        object? payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return Failure("El tipo de comando es obligatorio.", "invalid");
        }

        var screen = await _db.Screens.FindAsync(screenId);
        if (screen == null)
        {
            return Failure($"Pantalla '{screenId}' no encontrada.", "not-found");
        }

        var connectionId = _connectionManager.GetConnectionId(screenId);
        if (string.IsNullOrEmpty(connectionId))
        {
            return Failure(
                $"La pantalla '{screenId}' no se encuentra conectada al Hub (Status: '{screen.Status}').",
                "not-connected");
        }

        if (screen.Status != "online")
        {
            screen.Status = "online";
        }

        string? payloadJson = null;
        if (payload != null)
        {
            if (payload is JsonElement element)
            {
                payloadJson = element.GetRawText();
            }
            else
            {
                payloadJson = JsonSerializer.Serialize(payload);
            }
        }

        var command = new Command
        {
            Id = Guid.NewGuid(),
            ScreenId = screenId,
            Type = type,
            PayloadJson = payloadJson,
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.Commands.Add(command);

        var commandDto = new CommandDto
        {
            CommandId = command.Id,
            ScreenId = screenId,
            Type = command.Type,
            PayloadJson = command.PayloadJson,
            CreatedAt = command.CreatedAt
        };

        try
        {
            await _hubContext.Clients.Client(connectionId)
                .SendAsync("SendCommandToAgent", commandDto, cancellationToken);

            command.Status = "sent";
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Comando '{Type}' enviado a pantalla '{ScreenId}' (CommandId: {CommandId})",
                command.Type, screenId, command.Id);

            return new CommandDispatchResult
            {
                Success = true,
                CommandId = command.Id
            };
        }
        catch (Exception ex)
        {
            command.Status = "error";
            command.ResultDetail = $"Error al enviar comando: {ex.Message}";

            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // Persistencia best-effort del error.
            }

            _logger.LogError(ex, "Error al enviar comando {CommandId} a pantalla {ScreenId}", command.Id, screenId);

            return Failure("Error interno al transmitir el comando al agente.", "send-failed");
        }
    }

    private static CommandDispatchResult Failure(string error, string errorCode) => new()
    {
        Success = false,
        Error = error,
        ErrorCode = errorCode
    };
}

public class CommandDispatchResult
{
    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? Error { get; set; }

    public string ErrorCode { get; set; } = string.Empty;
}
