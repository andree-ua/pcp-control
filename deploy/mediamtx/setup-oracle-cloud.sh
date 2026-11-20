#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# PCP · Instalador de MediaMTX para Oracle Cloud Free Tier (Ubuntu)
#
# Instala Docker, genera la configuración del proyector, abre los puertos en
# iptables/ufw y levanta el contenedor en segundo plano. Es idempotente: se
# puede volver a ejecutar para actualizar la configuración o la imagen.
#
# Uso mínimo:
#   sudo bash setup-oracle-cloud.sh \
#     --server-url https://pcp-control.onrender.com
#
# Con HTTPS para el WHIP (recomendado si el panel es HTTPS):
#   sudo bash setup-oracle-cloud.sh \
#     --server-url https://pcp-control.onrender.com \
#     --domain pcp-media.duckdns.org
#
# Opciones:
#   --server-url URL     URL base del Pcp.Server en Render (obligatoria)
#   --domain DOMINIO     Dominio con DNS A a esta IP; activa Caddy + Let's Encrypt
#   --public-host IP     IP pública a anunciar (por defecto se autodetecta)
#   --auth-key CLAVE     Clave compartida; debe coincidir con Projector__AuthKey
#   --version X.Y.Z      Versión de MediaMTX (por defecto 1.21.4)
#   --install-dir RUTA   Directorio de instalación (por defecto /opt/pcp-mediamtx)
#   --no-firewall        No toca iptables/ufw (gestión manual)
#   -h, --help           Muestra esta ayuda
#
# Variables equivalentes: PCP_SERVER_URL, PCP_DOMAIN, PCP_PUBLIC_HOST,
# PCP_AUTH_KEY, PCP_MEDIAMTX_VERSION, PCP_INSTALL_DIR.
# ═══════════════════════════════════════════════════════════════════════════

set -Eeuo pipefail

SCRIPT_NAME="$(basename "$0")"

INSTALL_DIR="${PCP_INSTALL_DIR:-/opt/pcp-mediamtx}"
SERVER_URL="${PCP_SERVER_URL:-}"
PUBLIC_HOST="${PCP_PUBLIC_HOST:-}"
PCP_DOMAIN="${PCP_DOMAIN:-}"
AUTH_KEY="${PCP_AUTH_KEY:-}"
MEDIAMTX_VERSION="${PCP_MEDIAMTX_VERSION:-1.21.4}"
OPEN_FIREWALL=1

log()  { printf '\033[1;36m[PCP]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[PCP]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[PCP] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<'USAGE'
PCP · Instalador de MediaMTX para Oracle Cloud Free Tier (Ubuntu)

Uso mínimo:
  sudo bash setup-oracle-cloud.sh --server-url https://pcp-control.onrender.com

Con HTTPS para el WHIP (recomendado si el panel es HTTPS):
  sudo bash setup-oracle-cloud.sh \
    --server-url https://pcp-control.onrender.com \
    --domain pcp-media.duckdns.org

Opciones:
  --server-url URL     URL base del Pcp.Server en Render (obligatoria)
  --domain DOMINIO     Dominio con DNS A a esta IP; activa Caddy + Let's Encrypt
  --public-host IP     IP pública a anunciar (por defecto se autodetecta)
  --auth-key CLAVE     Clave compartida; debe coincidir con Projector__AuthKey
  --version X.Y.Z      Versión de MediaMTX (por defecto 1.21.4)
  --install-dir RUTA   Directorio de instalación (por defecto /opt/pcp-mediamtx)
  --no-firewall        No toca iptables/ufw (gestión manual)
  -h, --help           Muestra esta ayuda

Variables equivalentes: PCP_SERVER_URL, PCP_DOMAIN, PCP_PUBLIC_HOST,
PCP_AUTH_KEY, PCP_MEDIAMTX_VERSION, PCP_INSTALL_DIR.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --server-url)
            [[ $# -ge 2 ]] || die "Falta el valor de --server-url"
            SERVER_URL="$2"; shift 2 ;;
        --domain)
            [[ $# -ge 2 ]] || die "Falta el valor de --domain"
            PCP_DOMAIN="$2"; shift 2 ;;
        --public-host)
            [[ $# -ge 2 ]] || die "Falta el valor de --public-host"
            PUBLIC_HOST="$2"; shift 2 ;;
        --auth-key)
            [[ $# -ge 2 ]] || die "Falta el valor de --auth-key"
            AUTH_KEY="$2"; shift 2 ;;
        --version)
            [[ $# -ge 2 ]] || die "Falta el valor de --version"
            MEDIAMTX_VERSION="$2"; shift 2 ;;
        --install-dir)
            [[ $# -ge 2 ]] || die "Falta el valor de --install-dir"
            INSTALL_DIR="$2"; shift 2 ;;
        --no-firewall)
            OPEN_FIREWALL=0; shift ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            die "Opción desconocida: $1 (usa --help)" ;;
    esac
done

# ── Privilegios ─────────────────────────────────────────────────────────────
if [[ "${EUID}" -ne 0 ]]; then
    if command -v sudo >/dev/null 2>&1; then
        log "Se necesitan privilegios de root; reintentando con sudo..."
        exec sudo -E bash "$0" "$@"
    fi
    die "Ejecuta el script como root: sudo bash $SCRIPT_NAME ..."
fi

# ── Sistema operativo ───────────────────────────────────────────────────────
[[ -r /etc/os-release ]] || die "No se pudo leer /etc/os-release."
# shellcheck disable=SC1091
. /etc/os-release
if [[ "${ID:-}" != "ubuntu" && "${ID_LIKE:-}" != *debian* ]]; then
    die "Este instalador está pensado para Ubuntu 22.04/24.04 (detectado: ${PRETTY_NAME:-desconocido})."
fi

command -v apt-get >/dev/null 2>&1 || die "apt-get no está disponible."

# ── Validación de argumentos ────────────────────────────────────────────────
[[ -n "$SERVER_URL" ]] || die "Falta --server-url (URL base del Pcp.Server en Render)."
[[ "$SERVER_URL" =~ ^https?:// ]] || die "--server-url debe empezar por http:// o https://"

# Normaliza la URL de autorización (acepta la base o el endpoint completo).
SERVER_URL="${SERVER_URL%/}"
if [[ "$SERVER_URL" == */api/projector/auth ]]; then
    AUTH_URL="$SERVER_URL"
else
    AUTH_URL="${SERVER_URL}/api/projector/auth"
fi

if [[ -n "$PCP_DOMAIN" ]]; then
    PCP_DOMAIN="${PCP_DOMAIN#http://}"
    PCP_DOMAIN="${PCP_DOMAIN#https://}"
    PCP_DOMAIN="${PCP_DOMAIN%%/*}"
    [[ -n "$PCP_DOMAIN" ]] || die "--domain no es válido."
fi

[[ "$MEDIAMTX_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
    || die "--version debe tener el formato X.Y.Z (por ejemplo 1.21.4)."

# ── Paquetes base y Docker ──────────────────────────────────────────────────
log "Instalando paquetes base..."
export DEBIAN_FRONTEND=noninteractive
apt-get update -y -qq
apt-get install -y -qq ca-certificates curl gnupg iptables iproute2 jq >/dev/null

if ! command -v docker >/dev/null 2>&1; then
    log "Instalando Docker (script oficial)..."
    curl -fsSL https://get.docker.com | sh
else
    log "Docker ya está instalado: $(docker --version)"
fi

systemctl enable --now docker >/dev/null 2>&1 || true

# El daemon puede tardar unos segundos en aceptar conexiones tras la instalación.
for _ in $(seq 1 20); do
    if docker info >/dev/null 2>&1; then
        break
    fi
    sleep 1
done
docker info >/dev/null 2>&1 || die "El daemon de Docker no está disponible."

if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE=(docker-compose)
else
    log "Instalando el plugin docker compose..."
    apt-get install -y -qq docker-compose-plugin >/dev/null || true
    docker compose version >/dev/null 2>&1 \
        || die "No se pudo instalar 'docker compose'. Instálalo manualmente y reintenta."
    COMPOSE=(docker compose)
fi

# ── Detección de la IP pública ──────────────────────────────────────────────
detect_oci_public_ip() {
    # Metadata service de Oracle Cloud (v2). Devuelve la IP pública del VNIC,
    # que no aparece en las interfaces de la instancia (está detrás de NAT).
    curl -fsS --max-time 2 -H 'Authorization: Bearer Oracle' \
        http://169.254.169.254/opc/v2/vnics/ 2>/dev/null \
        | tr -d '\n' \
        | grep -o '"publicIp"[[:space:]]*:[[:space:]]*"[^"]*"' \
        | head -n1 | cut -d'"' -f4 || true
}

detect_public_ip() {
    curl -4 -fsS --max-time 8 https://api.ipify.org 2>/dev/null \
        || curl -4 -fsS --max-time 8 https://ifconfig.me/ip 2>/dev/null \
        || true
}

if [[ -z "$PUBLIC_HOST" ]]; then
    PUBLIC_HOST="$(detect_oci_public_ip)"
fi
if [[ -z "$PUBLIC_HOST" ]]; then
    PUBLIC_HOST="$(detect_public_ip)"
fi

if [[ -z "$PUBLIC_HOST" ]]; then
    die "No se pudo autodetectar la IP pública. Repite con --public-host TU_IP."
fi

# Solo se acepta host/IP, sin esquema ni ruta.
PUBLIC_HOST="${PUBLIC_HOST#http://}"
PUBLIC_HOST="${PUBLIC_HOST#https://}"
PUBLIC_HOST="${PUBLIC_HOST%%/*}"

log "IP pública para WebRTC: ${PUBLIC_HOST}"

# ── Clave compartida de autorización ────────────────────────────────────────
# Si no se pasa --auth-key se reutiliza la de una instalación anterior. No se
# genera una nueva por defecto para no romper la configuración de Render
# (Projector__AuthKey debe coincidir exactamente con la URL de autorización).
mkdir -p "$INSTALL_DIR"
if [[ -z "$AUTH_KEY" && -f "$INSTALL_DIR/.env" ]]; then
    AUTH_KEY="$(grep -E '^PCP_AUTH_KEY=' "$INSTALL_DIR/.env" | tail -n1 | cut -d= -f2- || true)"
fi

urlencode() {
    if command -v python3 >/dev/null 2>&1; then
        python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
    else
        printf '%s' "$1"
    fi
}

if [[ -n "$AUTH_KEY" ]]; then
    AUTH_URL="${AUTH_URL}?key=$(urlencode "$AUTH_KEY")"
fi

# ── Archivos de despliegue ──────────────────────────────────────────────────
log "Generando configuración en ${INSTALL_DIR}..."

cat > "$INSTALL_DIR/Dockerfile" <<'DOCKERFILE'
# syntax=docker/dockerfile:1
ARG MEDIAMTX_VERSION=1.21.4
FROM bluenviron/mediamtx:${MEDIAMTX_VERSION}

# Configuración del proyector PCP (auth HTTP delegada, RTSP/TCP, HLS y WHIP).
COPY mediamtx.yml /mediamtx.yml

EXPOSE 8554/tcp
EXPOSE 8889/tcp
EXPOSE 8189/udp
EXPOSE 8888/tcp

ENTRYPOINT ["/mediamtx"]
CMD ["/mediamtx.yml"]
DOCKERFILE

cat > "$INSTALL_DIR/mediamtx.yml" <<EOF
# Generado por setup-oracle-cloud.sh. Edita y vuelve a lanzar el script para
# aplicar cambios (o ejecuta: docker compose up -d --build).
logLevel: info
logDestinations: [stdout]

api: true
apiAddress: :9997

authMethod: http
authHTTPAddress: ${AUTH_URL}
authHTTPExclude:
  - action: api
  - action: metrics
  - action: pprof

rtsp: true
rtspAddress: :8554
rtspTransports: [tcp]
rtspEncryption: "no"

hls: true
hlsAddress: :8888
hlsVariant: lowLatency
hlsSegmentDuration: 1s
hlsPartDuration: 200ms
hlsSegmentCount: 7

webrtc: true
webrtcAddress: :8889
webrtcLocalUDPAddress: :8189
webrtcIPsFromInterfaces: false
webrtcAdditionalHosts: [${PUBLIC_HOST}]
webrtcAllowOrigins: ["*"]

paths:
  all_others:
    source: publisher
EOF

cat > "$INSTALL_DIR/docker-compose.yml" <<'COMPOSE'
name: pcp-mediamtx

services:
  mediamtx:
    build:
      context: .
      args:
        MEDIAMTX_VERSION: ${MEDIAMTX_VERSION:-1.21.4}
    image: pcp-mediamtx:${MEDIAMTX_VERSION:-1.21.4}
    container_name: pcp-mediamtx
    restart: unless-stopped

    ports:
      - "8554:8554/tcp"
      - "8889:8889/tcp"
      - "8189:8189/udp"
      - "8888:8888/tcp"
      - "127.0.0.1:9997:9997/tcp"

    environment:
      MTX_LOGLEVEL: ${MTX_LOGLEVEL:-info}
      MTX_AUTHHTTPADDRESS: ${PCP_AUTH_URL:?Falta PCP_AUTH_URL en .env}
      MTX_WEBRTCADDITIONALHOSTS: ${PUBLIC_HOST:?Falta PUBLIC_HOST en .env}
      MTX_WEBRTCIPSFROMINTERFACES: ${WEBRTC_IPS_FROM_INTERFACES:-false}

    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"
COMPOSE

cat > "$INSTALL_DIR/docker-compose.tls.yml" <<'TLS'
services:
  caddy:
    image: caddy:2-alpine
    container_name: pcp-caddy
    restart: unless-stopped
    network_mode: host
    environment:
      PCP_DOMAIN: ${PCP_DOMAIN:?Define PCP_DOMAIN}
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
      - caddy_config:/config
    depends_on:
      - mediamtx
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"

volumes:
  caddy_data:
  caddy_config:
TLS

cat > "$INSTALL_DIR/Caddyfile" <<'CADDY'
{$PCP_DOMAIN} {
    encode zstd gzip
    reverse_proxy 127.0.0.1:8889
    log {
        output stdout
        format console
    }
}
CADDY

cat > "$INSTALL_DIR/.env" <<EOF
MEDIAMTX_VERSION=${MEDIAMTX_VERSION}
PCP_AUTH_URL=${AUTH_URL}
PCP_AUTH_KEY=${AUTH_KEY}
PUBLIC_HOST=${PUBLIC_HOST}
WEBRTC_IPS_FROM_INTERFACES=false
MTX_LOGLEVEL=info
PCP_DOMAIN=${PCP_DOMAIN}
EOF

chmod 600 "$INSTALL_DIR/.env"
chmod 644 "$INSTALL_DIR/Dockerfile" "$INSTALL_DIR/mediamtx.yml" \
    "$INSTALL_DIR/docker-compose.yml" "$INSTALL_DIR/docker-compose.tls.yml" \
    "$INSTALL_DIR/Caddyfile"

# ── Comprobación del endpoint de autorización ───────────────────────────────
# Un 401/400/2xx significa que Render es alcanzable y la ruta existe. No se
# aborta si falla: el plan free de Render puede estar dormido y tardar en
# despertar; MediaMTX reintentará en cada publicación/lectura.
log "Comprobando el endpoint de autorización del Pcp.Server..."
AUTH_STATUS="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 20 \
    -X POST -H 'Content-Type: application/json' -d '{}' "$AUTH_URL" 2>/dev/null || true)"
case "$AUTH_STATUS" in
    2*|400|401)
        log "Endpoint de autorización accesible (HTTP ${AUTH_STATUS})." ;;
    *)
        warn "No se pudo validar ${AUTH_URL} (HTTP ${AUTH_STATUS:-sin respuesta})."
        warn "Revisa --server-url, que Render esté despierto y que Projector__AuthKey coincida."
        ;;
esac

# ── Firewall local (Oracle Ubuntu trae un REJECT en iptables) ───────────────
open_port() {
    local proto="$1" port="$2"

    if [[ "$OPEN_FIREWALL" -ne 1 ]]; then
        return 0
    fi

    if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
        ufw allow "${port}/${proto}" >/dev/null 2>&1 || true
    fi

    if ! iptables -C INPUT -p "$proto" --dport "$port" -j ACCEPT 2>/dev/null; then
        iptables -I INPUT 1 -p "$proto" --dport "$port" -j ACCEPT
        log "iptables: abierto ${port}/${proto}"
    fi
}

if [[ "$OPEN_FIREWALL" -eq 1 ]]; then
    # Oracle añade un REJECT al final de FORWARD. Docker publica los puertos con
    # DNAT y el tráfico del contenedor pasa por FORWARD, así que se permite el
    # reenvío (el host está dedicado a MediaMTX; INPUT sigue filtrado).
    if ! iptables -C FORWARD -j ACCEPT 2>/dev/null; then
        iptables -I FORWARD 1 -j ACCEPT
        log "iptables: reenvío Docker (FORWARD) permitido"
    fi

    open_port tcp 8554   # RTSP (agente)
    open_port tcp 8889   # WHIP (panel)
    open_port udp 8189   # ICE/WebRTC
    open_port tcp 8888   # HLS (respaldo)

    if [[ -n "$PCP_DOMAIN" ]]; then
        open_port tcp 80
        open_port tcp 443
    fi

    if ! dpkg -s iptables-persistent >/dev/null 2>&1; then
        echo "iptables-persistent iptables-persistent/autosave_v4 boolean true" | debconf-set-selections
        echo "iptables-persistent iptables-persistent/autosave_v6 boolean true" | debconf-set-selections
        apt-get install -y -qq iptables-persistent >/dev/null 2>&1 || true
    fi

    if command -v netfilter-persistent >/dev/null 2>&1; then
        netfilter-persistent save >/dev/null 2>&1 || true
    else
        mkdir -p /etc/iptables
        iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
    fi
fi

# ── Arranque del contenedor ─────────────────────────────────────────────────
COMPOSE_ARGS=(--env-file "$INSTALL_DIR/.env" -f "$INSTALL_DIR/docker-compose.yml")
if [[ -n "$PCP_DOMAIN" ]]; then
    COMPOSE_ARGS+=(-f "$INSTALL_DIR/docker-compose.tls.yml")
fi

cd "$INSTALL_DIR"

log "Construyendo y levantando MediaMTX (versión ${MEDIAMTX_VERSION})..."
"${COMPOSE[@]}" "${COMPOSE_ARGS[@]}" up -d --build

log "Esperando a que la API de MediaMTX responda..."
READY=0
for _ in $(seq 1 30); do
    if curl -fsS --max-time 2 http://127.0.0.1:9997/v3/paths/list >/dev/null 2>&1; then
        READY=1
        break
    fi
    sleep 2
done

if [[ "$READY" -ne 1 ]]; then
    warn "MediaMTX no respondió en 60 s. Últimos logs:"
    "${COMPOSE[@]}" "${COMPOSE_ARGS[@]}" logs --tail=40 mediamtx || true
    die "Revisa los logs con: cd ${INSTALL_DIR} && docker compose logs -f mediamtx"
fi

if [[ -n "$PCP_DOMAIN" ]]; then
    log "Caddy está solicitando el certificado HTTPS para ${PCP_DOMAIN}..."
    warn "La primera emisión puede tardar hasta 60 s mientras Let's Encrypt valida el dominio."
fi

# ── Resumen ─────────────────────────────────────────────────────────────────
if [[ -n "$PCP_DOMAIN" ]]; then
    WHIP_URL="https://${PCP_DOMAIN}"
else
    WHIP_URL="http://${PUBLIC_HOST}:8889"
fi

echo
echo "═══════════════════════════════════════════════════════════════════════"
echo " MediaMTX desplegado en ${INSTALL_DIR}"
echo "═══════════════════════════════════════════════════════════════════════"
echo " WHIP (panel):   ${WHIP_URL}"
echo " RTSP (agente):  rtsp://${PUBLIC_HOST}:8554"
echo " HLS (respaldo): http://${PUBLIC_HOST}:8888"
echo " API (local):    http://127.0.0.1:9997/v3/paths/list"
echo
echo " Variables para Render (Settings → Environment):"
echo "   Projector__PublicWhipBaseUrl=${WHIP_URL}"
echo "   Projector__PublicHlsBaseUrl=http://${PUBLIC_HOST}:8888"
echo "   Projector__PublicRtspUrl=rtsp://${PUBLIC_HOST}:8554"
if [[ -n "$AUTH_KEY" ]]; then
    echo "   Projector__AuthKey=${AUTH_KEY}"
else
    echo "   # Projector__AuthKey=  (opcional; usa --auth-key para activarla)"
fi
echo "   Projector__HealthCheckEnabled=true"
echo
echo " Abre en la Security List / NSG de la VCN de Oracle (además de iptables):"
echo "   TCP 8889, TCP 8554, TCP 8888, UDP 8189"
if [[ -n "$PCP_DOMAIN" ]]; then
    echo "   TCP 80 y TCP 443 (Caddy/Let's Encrypt)"
else
    echo
    warn "Sin --domain, el WHIP usa http://. Si el panel se sirve por HTTPS,"
    warn "el navegador bloqueará la publicación por contenido mixto. Usa un"
    warn "subdominio gratuito (DuckDNS) y repite con --domain."
fi
echo "═══════════════════════════════════════════════════════════════════════"
echo
echo " Logs:   cd ${INSTALL_DIR} && docker compose logs -f mediamtx"
echo " Estado: curl -s http://127.0.0.1:9997/v3/paths/list | jq"
echo " Parar:  cd ${INSTALL_DIR} && docker compose down"
