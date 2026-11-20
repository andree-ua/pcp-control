using System.Net;
using System.Net.Sockets;

namespace Pcp.Server.Services;

/// <summary>
/// Normalización y cálculo de los datos de red que reportan los agentes.
/// Centraliza las reglas para que registro, heartbeat y Wake-on-LAN validen igual.
/// </summary>
public static class NetworkData
{
    /// <summary>
    /// Convierte "aabbccddeeff", "aa-bb-cc-dd-ee-ff" o "AA:BB:..." en "AA:BB:CC:DD:EE:FF".
    /// Devuelve null si no son 6 octetos válidos o si es la MAC nula.
    /// </summary>
    public static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return null;

        var mac = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));

        // MAC nula: el adaptador no está realmente disponible para despertar.
        return mac == "00:00:00:00:00:00" ? null : mac;
    }

    /// <summary>
    /// Valida IP y máscara reportadas y devuelve la red en CIDR. Si la máscara no
    /// es válida se conserva la IP y se usa el CIDR informado por el agente.
    /// </summary>
    public static (string? LocalIp, string? SubnetMask, string? NetworkCidr) NormalizeNetwork(
        string? localIp,
        string? subnetMask,
        string? reportedCidr)
    {
        if (!IPAddress.TryParse(localIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return (null, null, null);
        }

        if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return (ip.ToString(), null, string.IsNullOrWhiteSpace(reportedCidr) ? null : reportedCidr.Trim());
        }

        var cidr = BuildCidr(ip, mask) ?? reportedCidr;
        return (ip.ToString(), mask.ToString(), string.IsNullOrWhiteSpace(cidr) ? null : cidr);
    }

    /// <summary>
    /// Broadcast dirigido de la red (192.168.1.10 / 255.255.255.0 → 192.168.1.255).
    /// Es la dirección que despierta equipos sin depender del broadcast limitado.
    /// </summary>
    public static string? BuildDirectedBroadcast(string? localIp, string? subnetMask)
    {
        if (!IPAddress.TryParse(localIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork) return null;

        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcast = new byte[4];

        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
        }

        return new IPAddress(broadcast).ToString();
    }

    private static string? BuildCidr(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4) return null;

        var prefix = 0;
        foreach (var b in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) == 0) break;
                prefix++;
            }
        }

        if (prefix == 0) return null;

        var network = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            network[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return $"{new IPAddress(network)}/{prefix}";
    }
}
