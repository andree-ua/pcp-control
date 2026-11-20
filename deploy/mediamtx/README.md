# MediaMTX para PCP — Proyector en la nube (Bloque 5)

Servidor de medios que permite **compartir la pantalla del operador en una TV**
sin depender de una PC local:

```
Navegador del panel ──WHIP (WebRTC)──▶ MediaMTX ──RTSP──▶ Pcp.Agent (LibVLC)
                                          └────HLS───▶ respaldo / diagnóstico
```

- El **panel** captura una pantalla con `getDisplayMedia` y la publica por **WHIP**.
- **MediaMTX** reempaqueta la señal y la sirve por RTSP (y HLS como respaldo).
- El **agente** reproduce el RTSP con caché baja; no abre ningún puerto.
- El **servidor PCP** crea los tokens efímeros y autoriza cada publicación/lectura.

No hay vídeo pasando por `Pcp.Server`: solo coordina sesiones, tokens y URLs.

> **Coste cero:** el despliegue recomendado es una instancia *Always Free* de
> **Oracle Cloud** con Ubuntu + Docker. El script `setup-oracle-cloud.sh` instala
> todo en un solo paso. No hace falta una PC local ni abrir puertos en casa.

---

## 1. Despliegue en Oracle Cloud Free Tier (recomendado, 0 €)

### 1.1. Crear la instancia

1. Entra en <https://cloud.oracle.com> y crea la cuenta (elige bien la **región
   principal**: no se puede cambiar y el *Free Tier* solo aplica en ella).
   Oracle solicita una tarjeta para verificar la identidad; el *Always Free* no
   genera cargos mientras no salgas de los límites de la cuenta.
2. Menú **Compute → Instances → Create instance**.
3. Rellena:
   - **Name**: `pcp-mediamtx`
   - **Image**: Canonical **Ubuntu 24.04** (o 22.04).
   - **Shape**: `VM.Standard.A1.Flex` con **1 OCPU / 6 GB** (Ampere, siempre
     gratis hasta 4 OCPU/24 GB) o, si no hay capacidad, `VM.Standard.E2.1.Micro`
     (AMD, 1 GB, siempre gratis).
   - **Primary VNIC**: asigna una **IPv4 pública**.
   - **SSH keys**: sube tu clave pública o genera un par y descarga la privada.
   - **Boot volume**: 50 GB (dentro de los 200 GB gratuitos).
4. Pulsa **Create** y anota la **IP pública**.
5. Recomendado: en **Networking → Reserved public IPs**, reserva esa IP y
   asígnala a la instancia. Una IP efímera puede cambiar si la detienes.

> El *Free Tier* incluye 10 TB/mes de salida. Un 1080p a 4–8 Mbps consume
> ~2–4 GB por hora, así que sobra para el proyector.

### 1.2. Abrir los puertos en OCI (Security List / NSG)

Esto es **independiente** del firewall de Ubuntu. Sin estos *ingress rules* el
tráfico no llega a la instancia:

| Puerto | Protocolo | Origen | Uso |
|---|---|---|---|
| `22` | TCP | Tu IP (o `0.0.0.0/0`) | SSH (normalmente ya existe) |
| `8889` | TCP | `0.0.0.0/0` | WHIP/WHEP — publica el panel |
| `8189` | UDP | `0.0.0.0/0` | ICE/WebRTC — **imprescindible** |
| `8554` | TCP | `0.0.0.0/0` | RTSP — lo lee el agente |
| `8888` | TCP | `0.0.0.0/0` | HLS — respaldo del agente |
| `80` | TCP | `0.0.0.0/0` | Solo con HTTPS (Let's Encrypt) |
| `443` | TCP | `0.0.0.0/0` | Solo con HTTPS (WHIP) |

Ruta en la consola: **Networking → Virtual Cloud Networks → tu VCN →
Security Lists → Default Security List → Add Ingress Rules**. Repite una regla
por puerto, con *Source Type* `CIDR`, *Source CIDR* `0.0.0.0/0` y el protocolo
y puerto indicados. Si usas **Network Security Groups**, añádelas también allí.

### 1.3. Ejecutar el instalador de un solo paso

Conéctate:

```bash
ssh -i ~/.ssh/tu-clave.pem ubuntu@IP_PUBLICA
```

Copia el script desde tu equipo (sustituye la ruta local y la IP):

```bash
scp -i ~/.ssh/tu-clave.pem deploy/mediamtx/setup-oracle-cloud.sh \
    ubuntu@IP_PUBLICA:/tmp/
```

Y ejecútalo **en la instancia**:

```bash
sudo bash /tmp/setup-oracle-cloud.sh \
  --server-url https://TU-SERVICIO.onrender.com
```

El script:

1. Instala Docker y el plugin `docker compose`.
2. Genera `/opt/pcp-mediamtx` con `Dockerfile`, `mediamtx.yml`,
   `docker-compose.yml` y `.env`.
3. Abre `8554/tcp`, `8889/tcp`, `8888/tcp` y `8189/udp` en `iptables`/`ufw` y
   los persiste tras reiniciar (Oracle Ubuntu trae un `REJECT` en iptables).
4. Construye la imagen (MediaMTX `1.21.4`) y levanta el contenedor en segundo
   plano con `restart: unless-stopped`.
5. Autodetecta la IP pública y la inyecta en `webrtcAdditionalHosts`.
6. Imprime las variables exactas que debes copiar en Render.

Opciones útiles:

```bash
# Ver ayuda
sudo bash /tmp/setup-oracle-cloud.sh --help

# Forzar la IP pública (si la autodetección falla)
sudo bash /tmp/setup-oracle-cloud.sh \
  --server-url https://TU-SERVICIO.onrender.com \
  --public-host 203.0.113.10

# Clave compartida para /api/projector/auth
sudo bash /tmp/setup-oracle-cloud.sh \
  --server-url https://TU-SERVICIO.onrender.com \
  --auth-key "una-clave-larga-y-aleatoria"
```

### 1.4. HTTPS gratuito para el WHIP (obligatorio si el panel es HTTPS)

El panel de producción se sirve por HTTPS. Un WHIP `http://IP:8889` se bloquea
por **contenido mixto**. Solución zero-cost: un subdominio gratis de
[DuckDNS](https://www.duckdns.org) y **Caddy** con Let's Encrypt automático.

1. En DuckDNS crea `pcp-media` (o el nombre que quieras) y apunta su registro
   **A** a la IP pública de la instancia.
2. Espera 1–2 minutos a que propague el DNS.
3. Vuelve a ejecutar el script añadiendo `--domain`:

```bash
sudo bash /tmp/setup-oracle-cloud.sh \
  --server-url https://TU-SERVICIO.onrender.com \
  --domain pcp-media.duckdns.org
```

El script añade el overlay `docker-compose.tls.yml` con **Caddy** en los puertos
80/443, que obtiene el certificado y hace de proxy del handshake WHIP hacia
`127.0.0.1:8889`. El vídeo sigue viajando por **UDP 8189** directamente entre el
navegador y MediaMTX (Caddy no lo toca).

> Alternativa sin abrir 80/443: **Cloudflare Tunnel** gratuito apuntando a
> `http://127.0.0.1:8889`. Es más complejo y añade un salto; para empezar,
> DuckDNS + Caddy es lo más simple.

### 1.5. Inyectar la IP pública en Render

En **Render → tu servicio de Pcp.Server → Environment**, añade y guarda:

| Variable | Valor |
|---|---|
| `Projector__PublicWhipBaseUrl` | `https://pcp-media.duckdns.org` (o `http://IP:8889` sin TLS) |
| `Projector__PublicHlsBaseUrl` | `http://IP_PUBLICA:8888` |
| `Projector__PublicRtspUrl` | `rtsp://IP_PUBLICA:8554` |
| `Projector__AuthKey` | La misma clave de `--auth-key` (si la usas) |
| `Projector__HealthCheckEnabled` | `true` |
| `Projector__HealthCheckRtsp` | `true` |

Después pulsa **Manual Deploy → Deploy latest commit** para que el servidor
reconstruya las URLs y reinicie la sonda de salud.

> **Importante:** las URLs son las que ven **el navegador** (WHIP) y **el agente**
> (RTSP/HLS), no las internas del contenedor. No uses `localhost` ni `10.x`.
> El script imprime este bloque al terminar; cópialo tal cual.

### 1.6. Verificar

En la instancia:

```bash
cd /opt/pcp-mediamtx
docker compose ps
docker compose logs -f mediamtx        # deja de seguir con Ctrl+C

curl -s http://127.0.0.1:9997/v3/paths/list | jq
```

Desde tu equipo (fuera de la VCN):

```bash
curl -I http://IP_PUBLICA:8889             # debe responder (404/401 también vale)
nc -vz IP_PUBLICA 8554                     # RTSP abierto
curl -I https://pcp-media.duckdns.org      # solo con --domain
```

En el panel, abre **Proyector** en una pantalla online y publica. Si todo está
bien, la TV muestra la emisión en 0,3–1 s (RTSP/TCP).

---

## 2. Despliegue en cualquier otro servidor con Docker

Requisitos: Docker + Docker Compose v2, IP pública y los puertos anteriores
abiertos. Sirve para un VPS, Fly.io (con contenedor), etc.

```bash
cd deploy/mediamtx
cp .env.example .env
# Edita .env:
#   PCP_AUTH_URL=https://TU-SERVICIO.onrender.com/api/projector/auth
#   PUBLIC_HOST=IP_PUBLICA
#   WEBRTC_IPS_FROM_INTERFACES=false
docker compose up -d --build
docker compose logs -f mediamtx
```

Con HTTPS (dominio con DNS A a la IP y puertos 80/443 abiertos):

```bash
PCP_DOMAIN=pcp-media.duckdns.org \
docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d --build
```

Para detenerlo:

```bash
docker compose down
```

### Estructura de los archivos

| Archivo | Uso |
|---|---|
| `Dockerfile` | Imagen autónoma basada en `bluenviron/mediamtx` con el YAML del proyecto |
| `docker-compose.yml` | Contenedor, puertos y variables de entorno |
| `docker-compose.tls.yml` | Overlay opcional con Caddy (HTTPS para WHIP) |
| `Caddyfile` | Sitio de Caddy (`{$PCP_DOMAIN}` → `127.0.0.1:8889`) |
| `mediamtx.yml` | Configuración base (auth delegada, RTSP/TCP, HLS, WHIP) |
| `setup-oracle-cloud.sh` | Instalador de un clic para Ubuntu/Oracle Free Tier |
| `.env.example` | Plantilla de variables del compose |

La configuración se **hornea en la imagen**. Si editas `mediamtx.yml`, aplica los
cambios con:

```bash
docker compose up -d --build
```

---

## 3. Puertos y firewall

| Puerto | Protocolo | Quién lo usa | ¿Público? |
|---|---|---|---|
| `8889` | TCP | WHIP/WHEP del navegador | Sí |
| `8189` | UDP | ICE/WebRTC (media) | Sí |
| `8554` | TCP | RTSP del agente | Sí |
| `8888` | TCP | HLS de respaldo / diagnóstico | Sí (opcional) |
| `9997` | TCP | API de MediaMTX | **No**: solo `127.0.0.1` |
| `80/443` | TCP | Caddy / Let's Encrypt | Solo con `--domain` |

Hay **dos firewalls** que abrir:

1. **OCI**: Security List / NSG de la VCN (tabla de la sección 1.2).
2. **Ubuntu**: `iptables` (Oracle trae reglas de `REJECT`) o `ufw`.

El script abre y persiste el segundo. Comprueba tras un reinicio:

```bash
sudo iptables -L INPUT -n -v --line-numbers | head -n 20
sudo netfilter-persistent save
```

Si haces el despliegue manual con Docker, ten en cuenta que Oracle también
bloquea el tráfico reenviado a los contenedores:

```bash
sudo iptables -I FORWARD 1 -j ACCEPT
sudo netfilter-persistent save
```

El script ya añade esa regla. Si añades puertos a mano con `iptables -I INPUT ...`,
guárdalos o se pierden al reiniciar.

---

## 4. Configurar el servidor PCP

En `Pcp.Server/appsettings.json` (o por variables de entorno):

```json
"Projector": {
  "Enabled": true,
  "PathPrefix": "pcp",
  "PublicWhipBaseUrl": "https://pcp-media.duckdns.org",
  "PublicHlsBaseUrl": "http://203.0.113.10:8888",
  "PublicRtspUrl": "rtsp://203.0.113.10:8554",
  "ReadUser": "pcp",
  "AuthKey": "",
  "PublisherGraceSeconds": 300,
  "SessionMaxHours": 12,
  "BufferMs": 400,
  "Volume": 80
}
```

Variables de entorno equivalentes:

```bash
Projector__PublicWhipBaseUrl=https://pcp-media.duckdns.org
Projector__PublicHlsBaseUrl=http://203.0.113.10:8888
Projector__PublicRtspUrl=rtsp://203.0.113.10:8554
Projector__AuthKey=cambiar-por-un-secreto-largo
```

Si defines `Projector:AuthKey`, la URL de autorización que usa MediaMTX debe
llevar la misma clave:

```yaml
authHTTPAddress: https://TU-SERVICIO.onrender.com/api/projector/auth?key=TU_CLAVE
```

El script la añade automáticamente cuando usas `--auth-key`.

---

## 5. Fail-safe: "Servidor de Streaming no disponible"

`Pcp.Server` no transporta vídeo, pero **sí comprueba que MediaMTX esté vivo**
para no dejar al agente reintentando contra una IP mal configurada o una
instancia apagada:

- **Al abrir el proyector** (`/projector/start`): sonda HTTP al WHIP y TCP al
  RTSP. Si falla, responde **503** con `"Servidor de Streaming no disponible"`.
  El modal del panel muestra ese texto exacto y **el agente nunca sale de idle**.
- **Al confirmar la publicación** (`/projector/live`): repite la sonda antes de
  ordenar `OpenProjector`. Si MediaMTX cayó entre ambos pasos, cierra la sesión
  y devuelve el mismo 503.
- **Mientras hay emisión**: un monitor revisa cada 20 s. Tras **2 fallos
  consecutivos**, cierra la sesión, envía `CloseProjector` al agente (que vuelve
  a idle) y retransmite `ProjectorStatusReceived` al panel; el panel muestra el
  toast **"Pantalla [id]: Servidor de Streaming no disponible"**.

Configuración (valores por defecto):

| Clave | Defecto | Uso |
|---|---|---|
| `Projector:HealthCheckEnabled` | `true` | Activa/desactiva todo el fail-safe |
| `Projector:HealthCheckTimeoutSeconds` | `4` | Timeout de cada sonda |
| `Projector:HealthCheckCacheSeconds` | `5` | Caché de la sonda (start/live fuerzan una nueva) |
| `Projector:HealthCheckRtsp` | `true` | Comprueba también `8554/tcp` |
| `Projector:HealthCheckIntervalSeconds` | `20` | Periodicidad del monitor |
| `Projector:HealthCheckFailuresBeforeClose` | `2` | Fallos seguidos para cerrar |
| `Projector:HealthCheckUrl` | *(vacío)* | URL HTTP alternativa (por defecto, el WHIP) |

> Si Render no puede alcanzar `8554/tcp` por políticas de salida pero los sticks
> sí, pon `Projector__HealthCheckRtsp=false` y deja solo la sonda HTTP.
> En desarrollo sin MediaMTX, usa `Projector__HealthCheckEnabled=false`.

---

## 6. Cómo se autoriza cada stream

`Pcp.Server` expone `POST /api/projector/auth`, que MediaMTX consulta antes de
aceptar la operación. El servidor responde `200` para permitir y `401` para
denegar.

| Acción | Credencial que exige |
|---|---|
| `publish` (WHIP del panel) | token de publicación en `Authorization: Bearer` o `?token=...` |
| `read` (RTSP del agente) | token de lectura en `?token=...` |
| `read` (HLS del agente) | token de lectura como contraseña Basic (`user:pass`) |
| `api`, `metrics`, `pprof` | permitidas (excluidas en `mediamtx.yml`) |

Las rutas tienen el formato `pcp/<screenId>` y cada sesión vive solo en memoria
del servidor. Al cerrar el modal, al desconectarse el agente o al caducar la
sesión, el servidor avisa al agente con `CloseProjector` y MediaMTX deja de
autorizar esa ruta.

---

## 7. Probar sin el panel

Publicar una prueba con ffmpeg (RTSP directo; requiere desactivar temporalmente
`authMethod` o autorizar la ruta):

```bash
ffmpeg -re -f lavfi -i testsrc=size=1280x720:rate=30 \
  -c:v libx264 -preset veryfast -tune zerolatency -f rtsp \
  rtsp://127.0.0.1:8554/pcp/prueba
```

Reproducir con:

```bash
ffplay -rtsp_transport tcp -fflags nobuffer -flags low_delay \
  -probesize 32 -analyzeduration 0 rtsp://IP_PUBLICA:8554/pcp/prueba
```

O abrir el HLS: `http://IP_PUBLICA:8888/pcp/prueba`.

> Para el flujo real usa siempre el panel: la autenticación HTTP rechaza rutas
> que no tengan una sesión y un token válidos.

---

## 8. Diagnóstico

| Síntoma | Causa habitual |
|---|---|
| El modal dice "Servidor de Streaming no disponible" | MediaMTX apagado, IP mal en Render o puerto 8889/8554 cerrado. |
| El navegador bloquea la publicación | Panel en HTTPS y WHIP en HTTP (contenido mixto). Usa `--domain` + Caddy. |
| WHIP devuelve 401 | Token caducado, ruta incorrecta o `AuthKey` distinta en Render y en MediaMTX. |
| WHIP conecta pero la TV no ve nada | El agente no llega a MediaMTX: revisa `8554`, credenciales y `agent.log`. |
| El vídeo se corta cada pocos segundos | `webrtcAdditionalHosts` con la IP privada o `8189/udp` cerrado en OCI/iptables. |
| La TV tarda 3–10 s | El agente usa HLS (respaldo) o hay pérdidas; comprueba RTSP/TCP. |
| La primera emisión tarda ~1 min | El servicio de Render (plan free) estaba dormido; la primera petición lo despierta. |
| Tras detener e iniciar la instancia cambia la IP | IP efímera. Reserva una IP pública en OCI y vuelve a ejecutar el script. |
| `docker compose` falla al arrancar | Mira `docker compose logs mediamtx`; el YAML generado está en `/opt/pcp-mediamtx`. |

Logs útiles:

```bash
docker compose logs -f mediamtx     # publicación/lectura y rechazos de auth
docker compose logs -f caddy        # solo con HTTPS
curl -s http://127.0.0.1:9997/v3/paths/list | jq
```

En el agente, `C:\ProgramData\PcpAgent\agent.log` registra:

```
[PROJECTOR] Emisión 'Pantalla 01 (proyector)' (rtsp, caché 400 ms).
[PROJECTOR] Reproduciendo por RTSP: rtsp (intento 1).
```

---

## 9. Seguridad y rendimiento

- Los tokens son aleatorios (192 bits), viven solo en memoria y caducan con la
  sesión. No se persisten en la base de datos.
- No expongas el puerto `9997` (API) a Internet: el compose lo ata a
  `127.0.0.1`. En Oracle, **no** lo añadas a la Security List.
- El panel es el único que publica; el agente solo lee. Un token de lectura no
  permite publicar en la ruta.
- RTSP viaja sin cifrar. El token protege la ruta, pero no el contenido. Para
  emisiones sensibles, usa una VPN/WireGuard o publica RTSP detrás de un proxy
  TLS (fuera del alcance del *Free Tier* básico).
- `webrtcAllowOrigins: ["*"]` es necesario porque el panel y MediaMTX viven en
  dominios distintos. Si los pones bajo el mismo dominio, restríngelo.
- Latencia típica: **0,3–1 s** con RTSP/TCP y 400 ms de caché. HLS de baja
  latencia añade ~1–2 s y es solo un respaldo.
- En la instancia micro, limita la calidad del panel (720p/30 fps) si compartes
  varios streams; MediaMTX no recodifica, así que el coste es sobre todo red.

---

## 10. Actualizar, reiniciar y desinstalar

```bash
# Actualizar la configuración y reconstruir
cd /opt/pcp-mediamtx
sudo bash /tmp/setup-oracle-cloud.sh --server-url ... --domain ...

# O manualmente, tras editar los archivos:
docker compose up -d --build

# Reiniciar solo el contenedor
docker compose restart mediamtx

# Ver uso de recursos
docker stats pcp-mediamtx

# Desinstalar
docker compose down
sudo rm -rf /opt/pcp-mediamtx
```

El contenedor tiene `restart: unless-stopped`, así que vuelve solo tras un
reinicio de la instancia (Docker está habilitado en el arranque). El watchdog
del agente y el proyector no dependen de que el operador esté conectado.

---

## 11. ¿Y Fly.io?

El `Dockerfile` y el `docker-compose.yml` sirven en cualquier host con Docker.
Fly.io ya no tiene un plan gratuito garantizado para cuentas nuevas (factura por
uso), así que para "cero coste" usa Oracle Always Free. Si ya tienes cuenta de
Fly.io, puedes desplegar la misma imagen con un `fly.toml` que exponga
`8554/tcp`, `8889/tcp`, `8888/tcp` y `8189/udp`, y definir
`MTX_AUTHHTTPADDRESS` y `MTX_WEBRTCADDITIONALHOSTS` con la IP/host que asigne
Fly. Recuerda que Fly factura por uso y que su IP pública puede requerir
configuración adicional.
