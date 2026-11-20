using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pcp.Agent.Services;

// Configuración del Host genérico de .NET 8 para el Agente PCP.
//
// Prioridad de configuración (de menor a mayor):
//   1. appsettings.json junto al ejecutable (opcional: el agente funciona sin él).
//   2. appsettings.{Environment}.json junto al ejecutable (opcional).
//   3. Variables de entorno estándar (Agent__ServerUrl, Agent__ScreenId, ...).
//   4. Variables con prefijo PCP_ (PCP_Agent__ServerUrl, ...) para el instalador.
//   5. Atajos PCP_SERVER_URL / PCP_SCREEN_ID / PCP_TOKEN.
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
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<FileService>();
        services.AddSingleton<SystemCommandService>();
        services.AddSingleton<VncServerManager>();
        services.AddSingleton<VncRemoteControlService>();
        services.AddHostedService<AgentService>();
    })
    .Build();

await host.RunAsync();
