using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Models;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Services;

/// <summary>
/// Orquesta el encendido remoto de una pantalla apagada.
///
/// Un Magic Packet no cruza Internet y el servidor vive en la nube, así que el
/// paquete lo emite otro agente online de la misma red (relay) al recibir el
/// comando "wake". El envío directo desde el servidor queda como modo opcional
/// para despliegues on-prem (Wol:DirectSendEnabled).
/// </summary>
public class WakeOnLanService
{
    private const int PacketLength = 6 + 16 * 6;

    // Cooldown por pantalla: evita ráfagas de Magic Packets desde el panel.
    private static readonly ConcurrentDictionary<string, DateTime> LastRequests =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;
    private readonly ScreenCommandService _commands;
    private readonly IScreenConnectionManager _connections;
    private readonly ILogger<WakeOnLanService> _logger;

    private readonly bool _enabled;
    private readonly bool _directSendEnabled;
    private readonly int _port;
    private readonly int _cooldownSeconds;
    private readonly string? _preferredRelay;

    public WakeOnLanService(
        AppDbContext db,
        ScreenCommandService commands,
        IScreenConnectionManager connections,
        IConfiguration config,
        ILogger<WakeOnLanService> logger)
    {
        _db = db;
        _commands = commands;
        _connections = connections;
        _logger = logger;

        _enabled = config.GetValue("Wol:Enabled", true);
        _directSendEnabled = config.GetValue("Wol:DirectSendEnabled", false);
        _port = config.GetValue("Wol:Port", 9);
        _cooldownSeconds = Math.Max(0, config.GetValue("Wol:CooldownSeconds", 20));

        var preferred = config["Wol:PreferredRelayScreenId"]?.Trim();
        _preferredRelay = string.IsNullOrWhiteSpace(preferred) ? null : preferred;
    }

    /// <summary>Encola el Magic Packet para la pantalla indicada.</summary>
    public async Task<WakeDispatchResult> WakeAsync(
        string screenId,
        string? relayScreenId,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return Fail("invalid", "El Wake-on-LAN está deshabilitado en el servidor (Wol:Enabled).");
        }

        var target = await _db.Screens.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == screenId, cancellationToken);

        if (target == null)
        {
            return Fail("not-found", $"Pantalla '{screenId}' no encontrada.");
        }

        if (!target.WolEnabled)
        {
            return Fail("invalid", $"El Wake-on-LAN está deshabilitado para la pantalla '{screenId}'.");
        }

        if (string.IsNullOrWhiteSpace(target.MacAddress))
        {
            return Fail("invalid",
                $"La pantalla '{screenId}' aún no ha reportado su MAC. Actualiza el agente al Bloque 2 " +
                "para que pueda despertarse.");
        }

        if (target.IsWired == false)
        {
            return Fail("invalid",
                $"La pantalla '{screenId}' está conectada por Wi-Fi: el Wake-on-LAN no funciona por " +
                "inalámbrico en la práctica.");
        }

        if (string.Equals(target.Status, "online", StringComparison.OrdinalIgnoreCase) ||
            _connections.GetConnectionId(screenId) != null)
        {
            return Fail("already-online", $"La pantalla '{screenId}' ya está encendida.");
        }

        var now = DateTime.UtcNow;
        if (_cooldownSeconds > 0 &&
            LastRequests.TryGetValue(screenId, out var last) &&
            now - last < TimeSpan.FromSeconds(_cooldownSeconds))
        {
            var remaining = (int)Math.Ceiling(
                (TimeSpan.FromSeconds(_cooldownSeconds) - (now - last)).TotalSeconds);

            return new WakeDispatchResult
            {
                Success = false,
                ErrorCode = "cooldown",
                Error = $"Espera {remaining} s antes de volver a pedir el encendido de '{screenId}'.",
                RetryAfterSeconds = Math.Max(1, remaining)
            };
        }

        var broadcast = NetworkData.BuildDirectedBroadcast(target.LocalIp, target.SubnetMask);

        // 1. Relay manual explícito desde el panel.
        if (!string.IsNullOrWhiteSpace(relayScreenId))
        {
            var manual = await _db.Screens.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == relayScreenId, cancellationToken);

            if (manual == null)
            {
                return Fail("invalid", $"La pantalla relay '{relayScreenId}' no existe.");
            }

            if (!IsReachable(manual))
            {
                return Fail("invalid", $"La pantalla relay '{relayScreenId}' no está online.");
            }

            return await DispatchToRelayAsync(manual, target, broadcast, now, cancellationToken);
        }

        // 2. Relay automático: otra pantalla online de la misma red.
        var relay = await FindAutomaticRelayAsync(target, cancellationToken);
        if (relay != null)
        {
            return await DispatchToRelayAsync(relay, target, broadcast, now, cancellationToken);
        }

        // 3. Envío directo (solo servidores desplegados dentro de la LAN).
        if (_directSendEnabled)
        {
            var (sent, error) = SendMagicPacket(
                target.MacAddress!, broadcast, target.LocalIp, _port, repeats: 3);

            if (!sent)
            {
                _logger.LogError("Fallo enviando Magic Packet directo a '{ScreenId}': {Error}", screenId, error);
                return Fail("send-failed", $"No se pudo emitir el Magic Packet: {error}");
            }

            LastRequests[screenId] = now;
            _logger.LogInformation("Magic Packet directo enviado a '{ScreenId}' ({Mac})", screenId, target.MacAddress);

            return Ok(new WakeResponseDto
            {
                Requested = true,
                Mode = "direct",
                MacAddress = target.MacAddress!,
                BroadcastAddress = broadcast,
                RetryAfterSeconds = _cooldownSeconds
            });
        }

        var network = string.IsNullOrWhiteSpace(target.NetworkCidr) ? "su red" : target.NetworkCidr;
        return Fail("no-relay",
            $"No hay ningún agente encendido en {network} que pueda emitir el Magic Packet. " +
            "Enciende al menos una pantalla de esa red (o configura un relay dedicado).");
    }

    private async Task<WakeDispatchResult> DispatchToRelayAsync(
        Screen relay,
        Screen target,
        string? broadcast,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var payload = new WolPayload
        {
            MacAddress = target.MacAddress!,
            BroadcastAddress = broadcast,
            TargetIp = target.LocalIp,
            TargetScreenId = target.Id,
            Repeats = 3
        };

        var dispatch = await _commands.SendAsync(relay.Id, "wake", payload, cancellationToken);
        if (!dispatch.Success)
        {
            return Fail(dispatch.ErrorCode, dispatch.Error);
        }

        LastRequests[target.Id] = now;

        _logger.LogInformation(
            "Magic Packet para '{Target}' encargado al relay '{Relay}' ({Mac}, red {Cidr})",
            target.Id, relay.Id, target.MacAddress, target.NetworkCidr ?? "desconocida");

        return Ok(new WakeResponseDto
        {
            Requested = true,
            Mode = "peer",
            RelayScreenId = relay.Id,
            MacAddress = target.MacAddress!,
            BroadcastAddress = broadcast,
            CommandId = dispatch.CommandId,
            RetryAfterSeconds = _cooldownSeconds
        });
    }

    /// <summary>Elige un agente online de la misma red, priorizando el cableado.</summary>
    private async Task<Screen?> FindAutomaticRelayAsync(Screen target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.NetworkCidr))
        {
            return null;
        }

        var candidates = await _db.Screens.AsNoTracking()
            .Where(s => s.Id != target.Id && s.NetworkCidr == target.NetworkCidr)
            .OrderByDescending(s => s.LastHeartbeat)
            .ToListAsync(cancellationToken);

        var reachable = candidates.Where(IsReachable).ToList();
        if (reachable.Count == 0)
        {
            return null;
        }

        if (_preferredRelay != null)
        {
            var preferred = reachable.FirstOrDefault(s => s.Id == _preferredRelay);
            if (preferred != null)
            {
                return preferred;
            }
        }

        return reachable
            .OrderByDescending(s => s.IsWired ?? false)
            .ThenByDescending(s => s.LastHeartbeat)
            .First();
    }

    private bool IsReachable(Screen screen) =>
        string.Equals(screen.Status, "online", StringComparison.OrdinalIgnoreCase) &&
        _connections.GetConnectionId(screen.Id) != null;

    /// <summary>
    /// Emite el Magic Packet desde el propio servidor. Solo tiene sentido si el
    /// servidor comparte LAN con las pantallas.
    /// </summary>
    private static (bool Ok, string? Error) SendMagicPacket(
        string macAddress,
        string? broadcastAddress,
        string? targetIp,
        int port,
        int repeats)
    {
        try
        {
            if (!TryBuildMagicPacket(macAddress, out var packet))
            {
                return (false, $"MAC inválida: '{macAddress}'.");
            }

            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

            var destinations = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
            if (IPAddress.TryParse(broadcastAddress, out var broadcast) &&
                broadcast.AddressFamily == AddressFamily.InterNetwork)
            {
                destinations.Add(new IPEndPoint(broadcast, port));
            }

            if (IPAddress.TryParse(targetIp, out var target) &&
                target.AddressFamily == AddressFamily.InterNetwork)
            {
                destinations.Add(new IPEndPoint(target, port));
            }

            for (var i = 0; i < Math.Clamp(repeats, 1, 5); i++)
            {
                foreach (var destination in destinations)
                {
                    udp.Send(packet, packet.Length, destination);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>6 bytes 0xFF + la MAC repetida 16 veces (102 bytes en total).</summary>
    private static bool TryBuildMagicPacket(string macAddress, out byte[] packet)
    {
        packet = Array.Empty<byte>();

        var parts = macAddress.Split(':', '-', '.');
        if (parts.Length != 6) return false;

        var mac = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out mac[i]))
            {
                return false;
            }
        }

        packet = new byte[PacketLength];
        for (var i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (var rep = 0; rep < 16; rep++)
        {
            Buffer.BlockCopy(mac, 0, packet, 6 + rep * 6, 6);
        }

        return true;
    }

    private static WakeDispatchResult Ok(WakeResponseDto response) => new()
    {
        Success = true,
        Response = response
    };

    private static WakeDispatchResult Fail(string errorCode, string? error) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Error = error
    };
}

/// <summary>Resultado de una petición de encendido remoto.</summary>
public sealed class WakeDispatchResult
{
    public bool Success { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Error { get; init; }

    public int RetryAfterSeconds { get; init; }

    public WakeResponseDto? Response { get; init; }
}
