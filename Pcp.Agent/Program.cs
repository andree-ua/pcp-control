using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pcp.Agent.Logging;
using Pcp.Agent.Services;

// Configuración del Host genérico de .NET 8 para el Agente PCP.
//
// El agente se publica como WinExe: no muestra consola. Todo el diagnóstico va a
// C:\ProgramData\PcpAgent\agent.log (configurable con Agent:LogPath), con rotación.
//
// Prioridad de configuración (de menor a mayor):
//   1. appsettings.json junto al ejecutable (opcional: el agente funciona sin él).
//   2. appsettings.{Environment}.json junto al ejecutable (opcional).
//   3. Variables de entorno estándar (Agent__ServerUrl, Agent__ScreenId, ...).
//   4. Variables con prefijo PCP_ (PCP_Agent__ServerUrl, ...).
//   5. Atajos del instalador: PCP_SERVER_URL / PCP_SCREEN_ID / PCP_SCREEN_NAME / PCP_TOKEN.
//   6. Argumentos de línea de comandos.
//
// Las variables de entorno y los atajos SIEMPRE ganan al appsettings.json. Así un
// instalador o una herramienta de gestión remota puede reconfigurar la flota sin
// editar archivos.
//
// El directorio de trabajo se fija al del ejecutable para que el agente pueda
// copiarse a cualquier carpeta del stick sin depender del directorio actual.
// En un single-file, AppContext.BaseDirectory apunta al directorio temporal de
// extracción; Environment.ProcessPath sí apunta al .exe real. Si se ejecuta con
// "dotnet Pcp.Agent.dll", ProcessPath es dotnet.exe y se usa BaseDirectory.
var processPath = Environment.ProcessPath;
var isDotnetHost = processPath is not null &&
    string.Equals(
        Path.GetFileNameWithoutExtension(processPath),
        "dotnet",
        StringComparison.OrdinalIgnoreCase);

var appDirectory = !string.IsNullOrWhiteSpace(processPath) && !isDotnetHost
    ? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
    : AppContext.BaseDirectory;

// ── Arranque del registro en archivo ────────────────────────────────────────
// Se construye una configuración mínima antes del host para conocer Agent:LogPath
// (el host construye después su propia configuración completa con las mismas fuentes).
var bootstrapConfig = new ConfigurationBuilder()
    .SetBasePath(appDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(
        $"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                        "Production"}.json",
        optional: true,
        reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddEnvironmentVariables(prefix: "PCP_")
    .AddCommandLine(args)
    .Build();

// La salida original se conserva como espejo: si alguien lanza el agente desde una
// consola (depuración), seguirá viendo los mensajes además de en el archivo.
AgentLog.Initialize(bootstrapConfig["Agent:LogPath"], Console.Out);
Console.SetOut(AgentLog.Out);
Console.SetError(AgentLog.Out);

// ── Instancia única por pantalla ────────────────────────────────────────────
// Evita que la tarea de inicio de sesión y el watchdog arranquen dos agentes a la
// vez. El ID de pantalla forma parte del mutex para conservar la simulación de
// varias pantallas en un mismo equipo de desarrollo.
var bootstrapScreenId = Environment.GetEnvironmentVariable("PCP_SCREEN_ID");
if (string.IsNullOrWhiteSpace(bootstrapScreenId))
{
    bootstrapScreenId = bootstrapConfig["Agent:ScreenId"];
}

var mutexName = $@"Local\PcpAgent-{(string.IsNullOrWhiteSpace(bootstrapScreenId) ? "screen" : bootstrapScreenId)}";
var singleInstance = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var isFirstInstance);

if (!isFirstInstance)
{
    AgentLog.Warn($"Ya hay un agente en ejecución para la pantalla '{bootstrapScreenId}'. Esta instancia termina.");
    singleInstance.Dispose();
    return;
}

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(appDirectory)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(appDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        config.AddJsonFile(
            $"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
            optional: true,
            reloadOnChange: false);

        // Variables estándar y con prefijo. Se añaden después del JSON para que
        // tengan prioridad incluso si cambia el orden por defecto del Host.
        config.AddEnvironmentVariables();
        config.AddEnvironmentVariables(prefix: "PCP_");

        var shortcuts = new Dictionary<string, string?>
        {
            ["Agent:ServerUrl"] = Environment.GetEnvironmentVariable("PCP_SERVER_URL"),
            ["Agent:ScreenId"] = Environment.GetEnvironmentVariable("PCP_SCREEN_ID"),
            ["Agent:Name"] = Environment.GetEnvironmentVariable("PCP_SCREEN_NAME"),
            ["Agent:Token"] = Environment.GetEnvironmentVariable("PCP_TOKEN")
        };

        config.AddInMemoryCollection(shortcuts
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value));

        // Los argumentos de línea de comandos conservan la prioridad más alta.
        config.AddCommandLine(args);

        // Normalizar la URL efectiva venga de donde venga: sin barra final y con
        // esquema https:// si el instalador solo escribió el dominio.
        var effective = config.Build();
        var normalized = new Dictionary<string, string?>();

        var serverUrl = NormalizeServerUrl(effective["Agent:ServerUrl"]);
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            normalized["Agent:ServerUrl"] = serverUrl;
        }

        var screenId = effective["Agent:ScreenId"]?.Trim();
        if (!string.IsNullOrWhiteSpace(screenId))
        {
            normalized["Agent:ScreenId"] = screenId;
        }

        if (normalized.Count > 0)
        {
            config.AddInMemoryCollection(normalized);
        }
    })
    .ConfigureLogging(logging =>
    {
        // Sin consola: solo el archivo (evita duplicar cada línea con formatos distintos).
        logging.ClearProviders();
        logging.AddProvider(new AgentFileLoggerProvider());
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<FileService>();
        services.AddSingleton<SystemCommandService>();
        services.AddSingleton<VncServerManager>();
        services.AddSingleton<VncRemoteControlService>();
        services.AddSingleton<AgentTokenProvider>();
        services.AddHostedService<AgentService>();
    })
    .Build();

// Diagnóstico para instalaciones masivas: deja claro qué configuración efectiva
// tiene el agente y si procede del entorno o del archivo.
var effectiveConfig = host.Services.GetRequiredService<IConfiguration>();
AgentLog.Info(string.Format(
    "[CONFIG] ScreenId='{0}' ServerUrl='{1}' Origen='{2}' Log='{3}'",
    effectiveConfig["Agent:ScreenId"] ?? "(sin definir)",
    effectiveConfig["Agent:ServerUrl"] ?? "(sin definir)",
    ResolveConfigurationSource(),
    AgentLog.FilePath ?? "(sin archivo)"));

try
{
    await host.RunAsync();
}
finally
{
    AgentLog.Info("Agente detenido.");
    AgentLog.Flush();

    try
    {
        singleInstance.ReleaseMutex();
    }
    catch (ApplicationException)
    {
        // El mutex ya no era nuestro (cierre forzado).
    }

    singleInstance.Dispose();
}

static string? NormalizeServerUrl(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var url = value.Trim().TrimEnd('/');
    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        url = "https://" + url;
    }

    return url;
}

static string ResolveConfigurationSource()
{
    var environmentValues = new[]
    {
        Environment.GetEnvironmentVariable("PCP_SERVER_URL"),
        Environment.GetEnvironmentVariable("PCP_SCREEN_ID"),
        Environment.GetEnvironmentVariable("PCP_Agent__ServerUrl"),
        Environment.GetEnvironmentVariable("PCP_Agent__ScreenId"),
        Environment.GetEnvironmentVariable("Agent__ServerUrl"),
        Environment.GetEnvironmentVariable("Agent__ScreenId")
    };

    return environmentValues.Any(value => !string.IsNullOrWhiteSpace(value))
        ? "Variables de entorno"
        : "appsettings.json / línea de comandos";
}
