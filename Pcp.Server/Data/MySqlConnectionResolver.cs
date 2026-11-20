using MySqlConnector;

namespace Pcp.Server.Data;

/// <summary>
/// Resuelve la cadena de conexión de MySQL desde configuración o variables de entorno
/// y normaliza las URI mysql:// que entregan proveedores cloud (Aiven, Clever Cloud).
/// </summary>
public static class MySqlConnectionResolver
{
    private static readonly string[] EnvironmentVariables =
    {
        "DATABASE_URL",
        "MYSQL_URL",
        "MYSQL_ADDON_URI",
        "CLEVER_CLOUD_MYSQL_URI"
    };

    public static string? Resolve(IConfiguration configuration)
    {
        var fromConfig = configuration.GetConnectionString("MySql");
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return Normalize(fromConfig);
        }

        // Compatibilidad con despliegues que reutilizan DefaultConnection para MySQL.
        var defaultConnection = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(defaultConnection) && IsMySqlUri(defaultConnection))
        {
            return Normalize(defaultConnection);
        }

        foreach (var variable in EnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Normalize(value);
            }
        }

        return null;
    }

    /// <summary>
    /// Convierte mysql://usuario:password@host:puerto/base?ssl-mode=REQUIRED
    /// a una cadena ADO.NET que entiende Pomelo/MySqlConnector.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (!IsMySqlUri(value))
        {
            return value;
        }

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = uri.Host,
            Port = (uint)(uri.IsDefaultPort || uri.Port <= 0 ? 3306 : uri.Port),
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            UserID = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            CharacterSet = "utf8mb4"
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 &&
                parts[0].Equals("ssl-mode", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<MySqlSslMode>(Uri.UnescapeDataString(parts[1]), ignoreCase: true, out var sslMode))
            {
                builder.SslMode = sslMode;
            }
        }

        return builder.ConnectionString;
    }

    public static bool IsMySqlUri(string value) =>
        value.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("mariadb://", StringComparison.OrdinalIgnoreCase);
}
