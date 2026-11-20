using System.Globalization;

namespace Pcp.Server.Services;

/// <summary>
/// Utilidades de días/horas para la programación de playlists (Bloque 4).
///
/// Los horarios se guardan como hora local y días ISO (1 = lunes … 7 = domingo)
/// porque es lo que entiende el operador. El servidor los convierte a UTC con la
/// zona configurada en <c>Scheduling:TimeZone</c> para fijar el instante exacto
/// que reciben los agentes.
/// </summary>
public static class ScheduleTime
{
    /// <summary>Zona por defecto si la configurada no existe en el sistema.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Día ISO: lunes = 1 … domingo = 7.</summary>
    public static int IsoDay(DayOfWeek day) => ((int)day + 6) % 7 + 1;

    /// <summary>Convierte "1,2,3,4,5" en un conjunto de días válidos (1-7).</summary>
    public static HashSet<int> ParseDays(string? days)
    {
        var result = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(days))
        {
            return result;
        }

        foreach (var part in days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) &&
                day is >= 1 and <= 7)
            {
                result.Add(day);
            }
        }

        return result;
    }

    /// <summary>Normaliza una lista de días a "1,2,3" (ordenada, sin duplicados).</summary>
    public static string FormatDays(IEnumerable<int>? days)
        => string.Join(",", (days ?? Enumerable.Empty<int>())
            .Where(day => day is >= 1 and <= 7)
            .Distinct()
            .OrderBy(day => day));

    public static bool IsDayEnabled(string? days, DayOfWeek day)
        => ParseDays(days).Contains(IsoDay(day));

    /// <summary>Valida y convierte "HH:mm" (24 h) a TimeSpan.</summary>
    public static bool TryParseStartTime(string? value, out TimeSpan time)
    {
        time = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TimeSpan.TryParseExact(
            value.Trim(),
            @"hh\:mm",
            CultureInfo.InvariantCulture,
            out time);
    }

    /// <summary>Formatea un TimeSpan como "HH:mm".</summary>
    public static string FormatStartTime(TimeSpan time)
        => $"{time.Hours:00}:{time.Minutes:00}";

    /// <summary>
    /// Próxima ejecución en UTC a partir de <paramref name="fromUtc"/>, o null si
    /// no hay ningún día válido. Recorre como máximo una semana.
    /// </summary>
    public static DateTime? NextRunUtc(
        string? days,
        string? startTime,
        TimeZoneInfo timeZone,
        DateTime fromUtc)
    {
        var enabledDays = ParseDays(days);
        if (enabledDays.Count == 0 || !TryParseStartTime(startTime, out var start))
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZone);

        for (var offset = 0; offset <= 7; offset++)
        {
            var day = localNow.Date.AddDays(offset);
            if (!enabledDays.Contains(IsoDay(day.DayOfWeek)))
            {
                continue;
            }

            var candidateLocal = DateTime.SpecifyKind(day + start, DateTimeKind.Unspecified);
            if (candidateLocal <= localNow)
            {
                continue;
            }

            return TimeZoneInfo.ConvertTimeToUtc(candidateLocal, timeZone);
        }

        return null;
    }
}
