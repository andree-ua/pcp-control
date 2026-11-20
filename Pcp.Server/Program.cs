using System.Net.WebSockets;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Pcp.Server.Data;
using Pcp.Server.Hubs;
using Pcp.Server.Models;
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
builder.Services.AddScoped<WakeOnLanService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSignalR(options =>
{
    // Las listas de archivos y los mensajes de control pueden superar el límite por defecto (32 KB).
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddControllers();

// ═══ 2b. Autenticación y autorización (Fase 3) ══════════════════════════════
// El panel usa una cookie HttpOnly que contiene el JWT; los agentes envían el
// token en la cabecera Authorization y, para WebSockets, en access_token.
var authIssuer = TokenService.ResolveIssuer(builder.Configuration);
var authAudience = TokenService.ResolveAudience(builder.Configuration);
var authSigningKey = TokenService.ResolveSigningKey(builder.Configuration, builder.Environment.IsProduction());
var authCookieName = builder.Configuration["Auth:CookieName"] ?? "pcp_auth";

builder.Services
    .AddAuthentication(options =>
    {
        // Selector dinámico: JWT para el panel/agentes nuevos y compatibilidad temporal
        // con agentes antiguos que aún usan ?screenId&token en el Hub.
        options.DefaultScheme = "PcpSmart";
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme("PcpSmart", "JWT o agente legacy", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var isHub = context.Request.Path.StartsWithSegments("/hubs/screen");
            var hasLegacyQuery = context.Request.Query.ContainsKey("screenId") &&
                                 context.Request.Query.ContainsKey("token");
            var hasAccessToken = context.Request.Query.ContainsKey("access_token");
            var hasAuthorizationHeader = !string.IsNullOrEmpty(context.Request.Headers.Authorization);
            var hasAuthCookie = context.Request.Cookies.ContainsKey(authCookieName);

            if (isHub && hasLegacyQuery && !hasAccessToken && !hasAuthorizationHeader && !hasAuthCookie)
            {
                return LegacyAgentAuthenticationHandler.SchemeName;
            }

            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, LegacyAgentAuthenticationHandler>(
        LegacyAgentAuthenticationHandler.SchemeName, _ => { })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authIssuer,
            ValidateAudience = true,
            ValidAudience = authAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSockets no pueden enviar cabeceras arbitrarias: SignalR y noVNC
                // adjuntan el token en la query.
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs/screen") || path.StartsWithSegments("/ws/vnc")))
                {
                    context.Token = accessToken;
                    return Task.CompletedTask;
                }

                // Resto de peticiones del panel: el JWT viene en la cookie HttpOnly.
                if (string.IsNullOrEmpty(context.Token))
                {
                    var cookieToken = context.Request.Cookies[authCookieName];
                    if (!string.IsNullOrEmpty(cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Límite de intentos de login por IP: frena la fuerza bruta sin afectar al resto.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// Límite de subida de archivos (multipart/form-data)
var maxUploadMb = builder.Configuration.GetValue<long?>("Files:MaxUploadMb") ?? 200;
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = Math.Max(1, maxUploadMb) * 1024L * 1024L;
});

// Documentación de endpoints con Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT del panel o del agente. Para el panel también se acepta la cookie HttpOnly 'pcp_auth'."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

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
await SeedAdminUserAsync(app);

// ═══ 5. Pipeline HTTP ═══════════════════════════════════════════════════════
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseCors(corsPolicyName);

// Defensa CSRF complementaria a SameSite: si el navegador envía Origin en una
// petición mutante, debe ser uno de los orígenes permitidos. Los agentes y las
// herramientas sin navegador no envían Origin y usan Authorization: Bearer.
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin, configuredOrigins, isDevelopment))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Origen no permitido." });
            return;
        }
    }

    await next();
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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
    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (!ctx.User.IsInRole(TokenService.AdminRole))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

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

/// <summary>
/// Crea el primer usuario administrador si la tabla está vacía.
/// En producción la contraseña es obligatoria (Auth__AdminPassword); en desarrollo
/// se usa "admin123" para no bloquear el arranque local.
/// </summary>
static async Task SeedAdminUserAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (await db.Users.AnyAsync())
    {
        return;
    }

    var username = (app.Configuration["Auth:AdminUsername"] ?? "admin").Trim().ToLowerInvariant();
    var password = app.Configuration["Auth:AdminPassword"];

    if (string.IsNullOrWhiteSpace(password))
    {
        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "No existe ningún usuario y falta Auth__AdminPassword. Configure " +
                "Auth__AdminUsername y Auth__AdminPassword para sembrar el administrador inicial.");
        }

        password = "admin123";
        app.Logger.LogWarning(
            "Usuario administrador de desarrollo creado: '{Username}' / 'admin123'. " +
            "Cambie la contraseña antes de exponer el servidor.", username);
    }
    else
    {
        app.Logger.LogInformation("Usuario administrador inicial creado: '{Username}'.", username);
    }

    db.Users.Add(new User
    {
        Id = Guid.NewGuid(),
        Username = username,
        DisplayName = "Administrador",
        PasswordHash = PasswordHasher.Hash(password),
        Role = TokenService.AdminRole,
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    });

    await db.SaveChangesAsync();
}
