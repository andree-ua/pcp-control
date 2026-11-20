using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Pcp.Agent.Logging;

namespace Pcp.Agent.Services;

/// <summary>
/// Datos de red de la interfaz física del equipo. Se envían al servidor para que
/// pueda emparejar relays de Wake-on-LAN dentro de la misma subred.
/// </summary>
public sealed class NetworkInfo
{
    /// <summary>MAC normalizada ("AA:BB:CC:DD:EE:FF").</summary>
    public string? MacAddress { get; init; }

    /// <summary>IPv4 de la interfaz.</summary>
    public string? LocalIp { get; init; }

    /// <summary>Máscara IPv4 ("255.255.255.0").</summary>
    public string? SubnetMask { get; init; }

    /// <summary>Red en notación CIDR ("192.168.1.0/24").</summary>
    public string? NetworkCidr { get; init; }

    /// <summary>true = cableada, false = Wi-Fi, null = desconocido.</summary>
    public bool? IsWired { get; init; }

    /// <summary>Nombre del adaptador elegido (solo diagnóstico).</summary>
    public string? InterfaceName { get; init; }

    /// <summary>Describe la interfaz para el log de arranque.</summary>
    public string Describe() =>
        $"Interfaz='{InterfaceName ?? "N/D"}' MAC={MacAddress ?? "N/D"} IP={LocalIp ?? "N/D"} " +
        $"Máscara={SubnetMask ?? "N/D"} Red={NetworkCidr ?? "N/D"} " +
        $"Cableada={(IsWired == true ? "Sí" : IsWired == false ? "No (Wi-Fi)" : "N/D")}";
}

/// <summary>
/// Detecta la interfaz de red física del equipo (MAC, IPv4, máscara y red CIDR),
/// descartando adaptadores virtuales (Hyper-V, VMware, VPN, WSL, Docker...).
///
/// La elección prioriza la interfaz que usa la ruta por defecto, luego la que tiene
/// puerta de enlace y, en empate, la cableada sobre la Wi-Fi: es la que puede emitir
/// un Magic Packet útil.
/// </summary>
public class NetworkInfoProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    // Marcadores de adaptadores no físicos. Se comparan en minúsculas contra
    // "nombre + descripción" del adaptador.
    private static readonly string[] VirtualMarkers =
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vbox",
        "tap-", "tap adapter", "tun", "wsl", "docker", "loopback", "bluetooth",
        "vpn", "wireguard", "openvpn", "zerotier", "tailscale", "npcap", "pseudo",
        "hamachi", "radmin", "anyconnect", "globalprotect", "fortinet", "sangfor",
        "teredo", "isatap", "6to4", "kernel debug", "microsoft wi-fi direct",
        "wi-fi direct", "miniport", "wan miniport"
    };

    private readonly IConfiguration _config;
    private readonly object _gate = new();

    private NetworkInfo _cached = new();
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public NetworkInfoProvider(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>Devuelve el último resultado (máximo 60 s de antigüedad).</summary>
    public NetworkInfo Get()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cachedAtUtc < CacheLifetime)
            {
                return _cached;
            }
        }

        return Refresh();
    }

    /// <summary>Fuerza una nueva detección (arranque, reconexión o cambio de red).</summary>
    public NetworkInfo Refresh()
    {
        lock (_gate)
        {
            _cached = Detect();
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    private NetworkInfo Detect()
    {
        var overrideMac = NormalizeMac(_config["Agent:Wol:MacOverride"]);
        var overrideName = _config["Agent:Wol:InterfaceName"]?.Trim();
        var defaultRouteIp = TryGetDefaultRouteAddress();

        var candidates = new List<Candidate>();
        var discarded = 0;

        foreach (var nic in SafeGetAllInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) { discarded++; continue; }
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel
                    or NetworkInterfaceType.Ppp) { discarded++; continue; }

                var label = $"{nic.Name} {nic.Description}".ToLowerInvariant();

                // Un Agent:Wol:InterfaceName explícito manda: permite elegir una
                // interfaz concreta aunque su nombre parezca virtual (útil en VMs,
                // docks USB-Ethernet y adaptadores con descripciones raras).
                var matchesOverride = !string.IsNullOrWhiteSpace(overrideName) &&
                    (nic.Name.Contains(overrideName, StringComparison.OrdinalIgnoreCase) ||
                     nic.Description.Contains(overrideName, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(overrideName) && !matchesOverride)
                {
                    discarded++;
                    continue;
                }

                if (!matchesOverride && VirtualMarkers.Any(marker => label.Contains(marker)))
                {
                    discarded++;
                    continue;
                }

                var mac = GetPhysicalMac(nic);
                if (mac == null) { discarded++; continue; }

                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address) &&
                    !IsApipa(a.Address));
                if (ipv4 == null) { discarded++; continue; }

                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null &&
                    !g.Address.Equals(IPAddress.Any) &&
                    !g.Address.Equals(IPAddress.IPv6Any));

                var score = 0;
                if (hasGateway) score += 100;
                if (defaultRouteIp != null && defaultRouteIp.Equals(ipv4.Address)) score += 200;
                if (!IsWireless(nic)) score += 20;
                if (nic.Speed > 0) score += (int)Math.Min(9, nic.Speed / 1_000_000_000L);

                candidates.Add(new Candidate(nic, ipv4, mac, score));
            }
            catch (Exception ex)
            {
                // Un adaptador problemático no debe impedir detectar los demás.
                AgentLog.Warn($"[RED] Adaptador ignorado por error: {ex.Message}");
            }
        }

        if (candidates.Count == 0)
        {
            AgentLog.Warn(
                $"[RED] No se encontró ninguna interfaz física utilizable ({discarded} descartadas). " +
                "Los adaptadores virtuales (Hyper-V, VMware, VPN, WSL) se ignoran a propósito; " +
                "configure Agent:Wol:InterfaceName o Agent:Wol:MacOverride si este equipo lo necesita.");

            // Aun sin IP, una MAC forzada sigue sirviendo para Wake-on-LAN.
            return new NetworkInfo { MacAddress = overrideMac };
        }

        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        var mask = TryGetMask(best.Ip);
        var cidr = mask != null ? BuildCidr(best.Ip.Address, mask) : null;

        return new NetworkInfo
        {
            MacAddress = overrideMac ?? best.Mac,
            LocalIp = best.Ip.Address.ToString(),
            SubnetMask = mask?.ToString(),
            NetworkCidr = cidr,
            IsWired = !IsWireless(best.Nic),
            InterfaceName = best.Nic.Name
        };
    }

    /// <summary>
    /// IP local de la interfaz que usa la ruta por defecto. Un "connect" UDP no
    /// envía tráfico: solo hace que el sistema elija la interfaz de salida.
    /// </summary>
    private static IPAddress? TryGetDefaultRouteAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<NetworkInterface> SafeGetAllInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            AgentLog.Error($"[RED] No se pudo enumerar los adaptadores de red: {ex.Message}");
            return Array.Empty<NetworkInterface>();
        }
    }

    private static string? GetPhysicalMac(NetworkInterface nic)
    {
        try
        {
            var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
            if (bytes == null || bytes.Length != 6) return null;

            // Todo ceros o dirección multicast: no es una MAC de interfaz física válida.
            if (bytes.All(b => b == 0) || (bytes[0] & 0x01) == 1) return null;

            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Normaliza "aabbccddeeff", "aa-bb-cc-dd-ee-ff" o "AA:BB:..." a "AA:BB:..."</summary>
    private static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12)
        {
            AgentLog.Warn($"[RED] Agent:Wol:MacOverride no es una MAC válida ('{value}'); se ignora.");
            return null;
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static IPAddress? TryGetMask(UnicastIPAddressInformation ip)
    {
        try
        {
            var mask = ip.IPv4Mask;
            return mask != null && !mask.Equals(IPAddress.Any) ? mask : null;
        }
        catch
        {
            // IPv4Mask no está disponible en todas las plataformas.
            return null;
        }
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

    private static bool IsWireless(NetworkInterface nic) =>
        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private sealed record Candidate(
        NetworkInterface Nic,
        UnicastIPAddressInformation Ip,
        string Mac,
        int Score);
}
