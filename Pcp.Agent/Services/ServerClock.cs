using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pcp.Agent.Logging;
using Pcp.Shared.Dtos;

namespace Pcp.Agent.Services;

/// <summary>
/// Reloj de referencia del servidor (Bloque 3: sincronización de reproducción).
///
/// Un stick puede tener el reloj desviado minutos (o días) sin que nadie lo note.
/// Para que dos pantallas arranquen el mismo vídeo en el mismo instante no basta
/// con "reproducir ya": hay que fijar un instante absoluto en el reloj del
/// servidor y que cada agente lo traduzca al suyo. Este servicio mide ese desfase
/// contra <c>GET /api/time</c> con una estimación tipo NTP:
///
///   offset = horaServidor - (t_envío + ida_y_vuelta / 2)
///
/// Se toman varias muestras y se conserva la de menor RTT, que es la que menos
/// error introduce. Un offset positivo significa que el reloj del agente va
/// atrasado respecto al servidor.
/// </summary>
public sealed class ServerClock : IDisposable
{
    /// <summary>Vida máxima del offset antes de volver a medirlo.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private const int Samples = 5;
    private const int SampleSpacingMs = 60;

    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerClock> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _offsetMs;
    private double _roundTripMs = double.NaN;
    private long _lastSyncUnixMs;
    private long _lastAttemptUnixMs;
    private bool _disposed;

    public ServerClock(IConfiguration configuration, ILogger<ServerClock> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Indica si ya hay una referencia de hora del servidor.</summary>
    public bool HasSync => Interlocked.Read(ref _lastSyncUnixMs) > 0;

    /// <summary>Desfase en ms entre el servidor y el agente (servidor - agente).</summary>
    public double OffsetMs => Interlocked.Read(ref _offsetMs);

    /// <summary>Desfase redondeado y acotado a int, listo para telemetría.</summary>
    public int OffsetMilliseconds
        => (int)Math.Clamp(Math.Round(OffsetMs), int.MinValue, int.MaxValue);

    /// <summary>RTT de la muestra que fijó el offset actual.</summary>
    public double RoundTripMs => _roundTripMs;

    /// <summary>Hora UTC corregida con el offset del servidor.</summary>
    public DateTime UtcNow => DateTime.UtcNow.AddMilliseconds(OffsetMs);

    /// <summary>Momento (hora local UTC) de la última sincronización correcta.</summary>
    public DateTime LastSyncUtc => HasSync
        ? DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastSyncUnixMs)).UtcDateTime
        : DateTime.MinValue;

    /// <summary>Indica si toca volver a medir el offset.</summary>
    public bool IsStale
    {
        get
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!HasSync)
            {
                // Sin referencia: se reintenta, pero con un minuto de espera entre
                // intentos para no inundar de peticiones un servidor inaccesible.
                return nowMs - Interlocked.Read(ref _lastAttemptUnixMs) > 60_000;
            }

            return nowMs - Interlocked.Read(ref _lastSyncUnixMs) > (long)MaxAge.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Traduce un instante del reloj del servidor (epoch Unix) al reloj local del
    /// agente. Es lo que se usa para programar el arranque del vídeo.
    /// </summary>
    public DateTime ToLocalUtc(long serverUnixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(serverUnixMs).UtcDateTime
            .AddMilliseconds(-OffsetMs);

    /// <summary>
    /// Milisegundos que faltan (según el reloj local) para un instante del servidor.
    /// Negativo significa que ya pasó.
    /// </summary>
    public long MillisecondsUntil(long serverUnixMs)
        => (long)Math.Round((ToLocalUtc(serverUnixMs) - DateTime.UtcNow).TotalMilliseconds);

    /// <summary>
    /// Mide el offset contra /api/time. Devuelve true si obtuvo al menos una
    /// muestra válida. No lanza: un fallo de red deja el offset anterior intacto.
    /// </summary>
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(
                ref _lastAttemptUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var serverUrl = (_configuration["Agent:ServerUrl"] ?? "http://localhost:5000").TrimEnd('/');
            var endpoint = $"{serverUrl}/api/time";

            double bestRtt = double.MaxValue;
            long bestOffset = 0;
            var received = 0;

            for (var i = 0; i < Samples && !cancellationToken.IsCancellationRequested; i++)
            {
                var startedTicks = Stopwatch.GetTimestamp();

                ServerTimeDto? sample;
                try
                {
                    sample = await _http.GetFromJsonAsync<ServerTimeDto>(endpoint, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Incluye timeouts (TaskCanceledException) y errores HTTP.
                    _logger.LogDebug("Muestra de /api/time descartada: {Message}", ex.Message);
                    sample = null;
                }

                var elapsedTicks = Stopwatch.GetTimestamp() - startedTicks;

                if (sample is not null && sample.UnixTimeMs > 0)
                {
                    var rttMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    var localMidpointMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - rttMs / 2.0;
                    var offset = sample.UnixTimeMs - localMidpointMs;

                    received++;

                    // La muestra con menor RTT es la que menos incertidumbre tiene.
                    if (rttMs < bestRtt)
                    {
                        bestRtt = rttMs;
                        bestOffset = (long)Math.Round(offset);
                    }
                }

                if (i < Samples - 1)
                {
                    try
                    {
                        await Task.Delay(SampleSpacingMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (received == 0)
            {
                AgentLog.Warn("[RELOJ] No se pudo medir el desfase con el servidor (/api/time).");
                return false;
            }

            Interlocked.Exchange(ref _offsetMs, bestOffset);
            _roundTripMs = bestRtt;
            Interlocked.Exchange(
                ref _lastSyncUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            AgentLog.Info(
                $"[RELOJ] Sincronizado con el servidor: offset {bestOffset:+0;-0;0} ms " +
                $"(RTT {bestRtt:F0} ms, {received}/{Samples} muestras).");

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Vuelve a medir solo si el offset caducó. Pensado para el bucle de heartbeat.</summary>
    public Task<bool> EnsureFreshAsync(CancellationToken cancellationToken = default)
        => IsStale ? SyncAsync(cancellationToken) : Task.FromResult(true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }
}
