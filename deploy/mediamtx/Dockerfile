# syntax=docker/dockerfile:1

# ═══════════════════════════════════════════════════════════════════════════
# PCP · MediaMTX del proyector (Bloque 5)
#
# Imagen autónoma lista para cualquier servidor cloud (Oracle Free Tier,
# Fly.io, VPS con Docker...). Parte de la imagen oficial, que es "scratch":
# solo contiene el binario y el mediamtx.yml por defecto.
#
# Configuración en tiempo de ejecución (docker-compose.yml o docker run -e):
#   - MTX_AUTHHTTPADDRESS          URL de /api/projector/auth del Pcp.Server
#   - MTX_WEBRTCADDITIONALHOSTS    IP pública que anuncia WebRTC (ICE)
#   - MTX_WEBRTCIPSFROMINTERFACES  false en cloud (solo IP pública)
# ═══════════════════════════════════════════════════════════════════════════

ARG MEDIAMTX_VERSION=1.21.4
FROM bluenviron/mediamtx:${MEDIAMTX_VERSION}

# La imagen oficial arranca con /mediamtx.yml. Se reemplaza por la configuración
# del proyecto (auth HTTP delegada, RTSP/TCP, HLS y WHIP).
COPY mediamtx.yml /mediamtx.yml

# Puertos públicos:
#   8554/tcp  RTSP  → lo lee el agente (LibVLC)
#   8889/tcp  WHIP  → publica el panel (WebRTC/HTTP)
#   8189/udp  ICE   → imprescindible para que el WHIP conecte
#   8888/tcp  HLS   → respaldo del agente y diagnóstico
EXPOSE 8554/tcp
EXPOSE 8889/tcp
EXPOSE 8189/udp
EXPOSE 8888/tcp

ENTRYPOINT ["/mediamtx"]
CMD ["/mediamtx.yml"]
