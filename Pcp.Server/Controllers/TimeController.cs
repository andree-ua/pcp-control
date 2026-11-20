using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pcp.Shared.Dtos;

namespace Pcp.Server.Controllers;

/// <summary>
/// Hora oficial del servidor. La usan los agentes para medir su desfase de reloj
/// antes de programar una reproducción sincronizada (Bloque 3).
///
/// Es anónimo a propósito: el agente necesita la referencia de tiempo incluso
/// antes de tener su JWT (arranque en frío) y la respuesta no revela nada
/// sensible. No se cachea para que el cálculo del offset sea exacto.
/// </summary>
[ApiController]
[Route("api/time")]
public class TimeController : ControllerBase
{
    /// <summary>GET /api/time — devuelve la hora UTC del servidor en epoch Unix (ms).</summary>
    [HttpGet]
    [AllowAnonymous]
    public ActionResult<ServerTimeDto> GetServerTime()
    {
        var now = DateTime.UtcNow;

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        return Ok(new ServerTimeDto
        {
            UnixTimeMs = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            UtcNow = now
        });
    }
}
