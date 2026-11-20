using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Pcp.Server.Data;
using Pcp.Server.Hubs;
using Pcp.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ═══ 1. Configuración de Base de Datos ═══════════════════════════════════════
// Producción: MySQL 8.x (Pomelo). Desarrollo local: SQLite (fallback).
// El proveedor se elige con Database:Provider (o Database__Provider) y, si no
// se especifica, por la presencia de una cadena de conexión MySQL.
// En la nube la cadena llega por variables de entorno (ConnectionStrings__MySql,
// DATABASE_URL, MYSQL_URL o MYSQL_ADDON_URI de Clever Cloud).
var databaseProvider = ResolveDatabaseProvider(builder.Configuration, builder.Environment);
var useMySql = databaseProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

var sqliteConnectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=pcp.db";

var mySqlConnectionString = useMySql ? MySqlConnectionResolver.Resolve(builder.Configuration) : null;

if (useMySql && string.IsNullOrWhiteSpace(mySqlConnectionString))
{
    throw new InvalidOperationException(
        "No se encontró la cadena de conexión de MySQL. Configure ConnectionStrings__MySql " +
        "o la variable DATABASE_URL / MYSQL_URL / MYSQL_ADDON_URI.");
}

var mySqlServerVersion = ParseMySqlVersion(builder.Configuration["Database:MySqlVersion"]);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useMySql)
    {
        options.UseMySql(
            mySqlConnectionString!,
            new MySqlServerVersion(mySqlServerVersion),
            mySql => mySql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null));
    }
    else
    {
        options.UseSqlite(sqliteConnectionString);
    }
});

// ═══ 2. Servicios del Dominio y Comunicación ════════════════════════════════
builder.Services.AddSingleton<IScreenConnectionManager, ScreenConnectionManager>();
builder.Services.AddSingleton<IFileRequestManager, FileRequestManager>();
// Sesiones de control remoto (túnel VNC panel ↔ agente)
builder.Services.AddSingleton<IVncSessionManager, VncSessionManager>();
builder.Services.AddScoped<ScreenCommandService>();
builder.Services.AddSignalR(options =>
{
    // Las listas de archivos y los mensajes de control pueden superar el límite por defecto (32 KB).
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddControllers();

// Límite de subida de archivos (multipart/form-data)
var maxUploadMb = builder.Configuration.GetValue<long?>("Files:MaxUploadMb") ?? 200;
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = Math.Max(1, maxUploadMb) * 1024L * 1024L;
});

// Documentación de endpoints con Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Tarea en segundo plano para auditoría de timeout de pantallas (offline detection)
builder.Services.AddHostedService<OfflineCheckerService>();

// ═══ 3. Configuración de CORS y proxy ═══════════════════════════════════════
// Los orígenes permitidos se configuran con Cors:AllowedOrigins (en producción)
// o Cors__AllowedOrigins__0, __1... por variables de entorno. En desarrollo se
// siguen aceptando localhost/127.0.0.1 automáticamente.
const string corsPolicyName = "PcpCorsPolicy";
var configuredOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        policy.SetIsOriginAllowed(origin => IsAllowedOrigin(origin, configuredOrigins, isDevelopment))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Detrás de un proxy/balanceador (Aiven, Clever Cloud, nginx, etc.) hay que
// confiar en X-Forwarded-Proto para que el panel reciba URLs ws/wss correctas.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// La nube suele asignar el puerto en PORT. Si no existe, se respeta la
// configuración estándar de ASP.NET Core (ASPNETCORE_URLS / launchSettings).
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out var parsedPort) && parsedPort > 0)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

var app = builder.Build();

// ═══ 4. Inicialización de la Base de Datos ══════════════════════════════════
if (useMySql)
{
    app.Logger.LogInformation("Base de datos: MySQL {Version} (migraciones EF Core)", mySqlServerVersion);
}
else
{
    app.Logger.LogInformation("Base de datos: SQLite ({ConnectionString})", sqliteConnectionString);
}

if (isDevelopment == false && configuredOrigins.Length == 0)
{
    app.Logger.LogWarning(
        "Cors:AllowedOrigins está vacío: el panel web no podrá llamar a la API desde otro dominio. " +
        "Configure Cors__AllowedOrigins__0=https://tu-panel.example.com");
}

// MySQL en la nube puede tardar en aceptar conexiones durante el arranque:
// se reintenta la migración antes de dejar el servidor inoperativo.
await InitializeDatabaseAsync(app, useMySql);

// ═══ 5. Pipeline HTTP ═══════════════════════════════════════════════════════
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseCors(corsPolicyName);

// ═══ 6. Túnel de escritorio remoto (VNC sobre WebSocket) ════════════════════
// El panel y el agente se conectan aquí; el servidor solo bombea bytes (RFB).
//
// Nota sobre TLS: Kestrel acepta WebSockets ws:// sobre HTTP sin ninguna opción
// adicional; no existe una negociación TLS que habilitar para loopback. El esquema
// del túnel se elige en RemoteControlController (ws con http, wss con https).
// El error "Sorry, loopback connections are not enabled" NO lo produce Kestrel ni
// el WebSocket: lo emite TightVNC al rechazar conexiones desde 127.0.0.1 cuando su
// valor de registro AllowLoopback no vale 1. Lo configura el agente en
// Pcp.Agent/Services/VncServerManager.cs.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

// Extremo del panel web (noVNC)
app.MapGet("/ws/vnc/{sessionId}", async (HttpContext ctx, string sessionId, IVncSessionManager sessions) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var session = sessions.Get(sessionId);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (session.PanelSocket is not null)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await sessions.AttachPanelAsync(session, socket, ctx.RequestAborted);
});

// Extremo del agente: túnel saliente autenticado con un secreto por sesión
app.MapGet("/ws/vnc-agent/{sessionId}", async (
    HttpContext ctx,
    string sessionId,
    string? secret,
    IVncSessionManager sessions) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var session = sessions.Get(sessionId);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!sessions.TryClaimAgent(session, secret ?? string.Empty) || session.AgentSocket is not null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await sessions.AttachAgentAsync(session, socket, ctx.RequestAborted);
});

app.MapControllers();
app.MapHub<ScreenHub>("/hubs/screen");

app.Run();

// ═══ Funciones auxiliares ═══════════════════════════════════════════════════
static string ResolveDatabaseProvider(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configured = configuration["Database:Provider"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.Trim();
    }

    if (!string.IsNullOrWhiteSpace(MySqlConnectionResolver.Resolve(configuration)))
    {
        return "MySql";
    }

    // En producción se exige MySQL: si falta la cadena, el arranque falla con
    // un mensaje accionable en lugar de crear silenciosamente un pcp.db local.
    return environment.IsProduction() ? "MySql" : "Sqlite";
}

static Version ParseMySqlVersion(string? value)
{
    if (!string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out var parsed))
    {
        return parsed;
    }

    return new Version(8, 0, 36);
}

static bool IsAllowedOrigin(string? origin, string[] allowedOrigins, bool isDevelopment)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return false;
    }

    var candidate = origin.TrimEnd('/');
    foreach (var allowed in allowedOrigins)
    {
        if (string.Equals(allowed.TrimEnd('/'), candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    if (!isDevelopment)
    {
        return false;
    }

    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.Host == "localhost" || uri.Host == "127.0.0.1";
}

static async Task InitializeDatabaseAsync(WebApplication app, bool useMySql)
{
    var maxAttempts = useMySql ? 10 : 1;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (useMySql)
            {
                // MySQL se crea y actualiza exclusivamente con migraciones EF Core.
                await db.Database.MigrateAsync();
            }
            else
            {
                // SQLite se mantiene con EnsureCreated para no romper el pcp.db local
                // (las migraciones generadas con Pomelo son específicas de MySQL).
                await db.Database.EnsureCreatedAsync();
            }

            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            app.Logger.LogWarning(
                ex,
                "No se pudo inicializar MySQL (intento {Attempt}/{Max}). Reintentando en 5 s...",
                attempt,
                maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
