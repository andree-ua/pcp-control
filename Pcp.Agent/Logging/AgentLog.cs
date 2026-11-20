using System.Text;
using Microsoft.Extensions.Logging;

namespace Pcp.Agent.Logging;

/// <summary>
/// Registro de actividad del agente en archivo, seguro para varios hilos.
///
/// El agente se publica como <c>WinExe</c> (sin consola), así que este logger es la
/// única salida de diagnóstico en producción:
///   - Los <c>Console.WriteLine</c> heredados se redirigen aquí con <c>Console.SetOut</c>.
///   - Los <c>ILogger</c> de Microsoft.Extensions.Logging se capturan con
///     <see cref="AgentFileLoggerProvider"/>.
///   - Rota el archivo a los 5 MB y conserva 3 copias (<c>agent.1.log</c> ... <c>agent.3.log</c>).
/// </summary>
public static class AgentLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxBackups = 3;

    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static TextWriter? _mirror;
    private static TextWriter? _out;
    private static int _writesSinceCheck;

    /// <summary>Archivo de log activo, o <c>null</c> si no se pudo abrir ninguno.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>TextWriter que envía cada línea al archivo (se usa para Console.SetOut).</summary>
    public static TextWriter Out => _out ?? TextWriter.Null;

    /// <summary>
    /// Abre el archivo de log. Si la ruta configurada no se puede escribir (por ejemplo,
    /// sin permisos sobre ProgramData) cae a <c>%LOCALAPPDATA%\Pcp\logs\agent.log</c>.
    /// </summary>
    /// <param name="configuredPath">Ruta de <c>Agent:LogPath</c>; vacía usa el valor por defecto.</param>
    /// <param name="mirror">Salida original de consola para duplicar en desarrollo.</param>
    public static void Initialize(string? configuredPath = null, TextWriter? mirror = null)
    {
        lock (Gate)
        {
            _mirror = mirror;

            var path = ResolvePath(configuredPath);
            if (!TryOpen(path))
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Pcp",
                    "logs",
                    "agent.log");

                if (!TryOpen(fallback))
                {
                    FilePath = null;
                }
            }

            _out = new LogTextWriter();
        }

        if (FilePath is null)
        {
            Write("WARN", "No se pudo abrir el archivo de log; el agente continuará sin registro en disco.");
        }
        else
        {
            Write("INFO", $"Registro iniciado en {FilePath}");
        }
    }

    private static bool TryOpen(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rotate(path);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            return true;
        }
        catch
        {
            _file = null;
            return false;
        }
    }

    private static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        return Path.Combine(programData, "PcpAgent", "agent.log");
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
        {
            return;
        }

        var oldest = $"{path}.{MaxBackups}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaxBackups - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index + 1}", true);
            }
        }

        File.Move(path, $"{path}.1", true);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message) => Write("DEBUG", message);

    /// <summary>Escribe una línea con marca de tiempo y nivel.</summary>
    public static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        lock (Gate)
        {
            MaybeRotate();

            try
            {
                _file?.WriteLine(line);
            }
            catch
            {
                // El disco lleno o un archivo bloqueado no deben tumbar al agente.
            }

            try
            {
                _mirror?.WriteLine(line);
            }
            catch
            {
                // Sin consola adjunta (WinExe): el espejo no es imprescindible.
            }
        }
    }

    /// <summary>
    /// Comprueba el tamaño cada 200 líneas y rota sin reiniciar el agente.
    /// Debe llamarse con <see cref="Gate"/> tomado.
    /// </summary>
    private static void MaybeRotate()
    {
        if (++_writesSinceCheck < 200)
        {
            return;
        }

        _writesSinceCheck = 0;

        if (_file is null || FilePath is null)
        {
            return;
        }

        try
        {
            if (_file.BaseStream.Length < MaxFileBytes)
            {
                return;
            }

            _file.Dispose();
            _file = null;
            Rotate(FilePath);

            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            // Si la rotación falla se sigue escribiendo en el archivo actual.
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            try { _file?.Flush(); } catch { }
        }
    }

    /// <summary>TextWriter que convierte las escrituras de consola en líneas de log.</summary>
    private sealed class LogTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_buffer)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            lock (_buffer)
            {
                _buffer.Append(value);
            }
        }

        public override void WriteLine()
        {
            FlushBuffer(string.Empty);
        }

        public override void WriteLine(string? value)
        {
            FlushBuffer(value ?? string.Empty);
        }

        private void FlushBuffer(string value)
        {
            string text;
            lock (_buffer)
            {
                _buffer.Append(value);
                text = _buffer.ToString();
                _buffer.Clear();
            }

            AgentLog.Write("INFO", text);
        }
    }
}

/// <summary>Proveedor de <see cref="ILogger"/> que escribe en <see cref="AgentLog"/>.</summary>
public sealed class AgentFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AgentFileLogger(categoryName);

    public void Dispose()
    {
        AgentLog.Flush();
    }

    private sealed class AgentFileLogger : ILogger
    {
        private readonly string _category;

        public AgentFileLogger(string category)
        {
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" | {exception.GetType().Name}: {exception.Message}";
            }

            AgentLog.Write(LevelName(logLevel), $"{_category}: {message}");
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "INFO"
        };
    }
}
