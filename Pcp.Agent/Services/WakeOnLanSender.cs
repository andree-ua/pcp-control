using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pcp.Agent.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Emite Magic Packets (Wake-on-LAN) en nombre del servidor.
///
/// El servidor vive en la nube y no puede alcanzar la LAN del stick, así que le
/// encarga el paquete a otro agente de la misma red mediante el comando "wake".
/// Este servicio solo construye el paquete y lo difunde por broadcast local.
/// </summary>
public class WakeOnLanSender
{
    private const int PacketLength = 6 + 16 * 6;

    private readonly IConfiguration _config;
    private readonly ILogger<WakeOnLanSender> _logger;

    public WakeOnLanSender(IConfiguration config, ILogger<WakeOnLanSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Difunde el Magic Packet del payload. Devuelve (éxito, detalle).</summary>
    public (bool Success, string Detail) Send(WolPayload payload)
    {
        if (!TryBuildMagicPacket(payload.MacAddress, out var packet))
        {
            return (false, $"MAC inválida para Wake-on-LAN: '{payload.MacAddress}'.");
        }

        var port = _config.GetValue("Agent:Wol:Port", 9);
        var repeats = Math.Clamp(payload.Repeats <= 0 ? 3 : payload.Repeats, 1, 5);

        var destinations = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };

        if (IPAddress.TryParse(payload.BroadcastAddress, out var broadcast) &&
            broadcast.AddressFamily == AddressFamily.InterNetwork)
        {
            destinations.Add(new IPEndPoint(broadcast, port));
        }

        // Enviar también a la última IP conocida ayuda cuando el switch no
        // reenvía el broadcast dirigido.
        if (IPAddress.TryParse(payload.TargetIp, out var target) &&
            target.AddressFamily == AddressFamily.InterNetwork)
        {
            destinations.Add(new IPEndPoint(target, port));
        }

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

            for (var i = 0; i < repeats; i++)
            {
                foreach (var destination in destinations)
                {
                    udp.Send(packet, packet.Length, destination);
                }
            }

            var detail =
                $"Magic packet enviado a {string.Join(", ", destinations.Select(d => $"{d.Address}:{d.Port}"))} " +
                $"para {payload.MacAddress}" +
                (string.IsNullOrWhiteSpace(payload.TargetScreenId) ? "." : $" (pantalla {payload.TargetScreenId}).");

            AgentLog.Info($"[WOL] {detail}");
            return (true, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error emitiendo el Magic Packet");
            AgentLog.Error($"[WOL] Error emitiendo el Magic Packet: {ex.Message}");
            return (false, $"Error emitiendo el Magic Packet: {ex.Message}");
        }
    }

    /// <summary>6 bytes 0xFF + la MAC repetida 16 veces (102 bytes en total).</summary>
    private static bool TryBuildMagicPacket(string? macAddress, out byte[] packet)
    {
        packet = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(macAddress)) return false;

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
}
