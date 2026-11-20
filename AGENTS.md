# AGENTS.md — PCP (Panel de Control de Pantallas)

Mapa de arquitectura y reglas de negocio del proyecto. **Léelo antes de tocar código.**
Última actualización: 2026-11-20 (Fase 0–3 completas + Bloque 1: JWT, roles y login del panel + Bloque 2: Wake-on-LAN operativo con relay entre agentes y encendido masivo por grupo + Bloque 3: sincronización de reproducción con reloj del servidor + Bloque 4: playlists y programación de horarios + Bloque 5: proyector en directo con MediaMTX, WHIP desde el panel y RTSP en el agente).

---

## 1. Visión general

Sistema para administrar remotamente mini-PCs Windows ("sticks") conectadas a televisores.
Tres procesos independientes se comunican por HTTP REST + SignalR:

```
Pcp.Web (Next.js, 3000/3001)  ──REST──▶  Pcp.Server (ASP.NET Core, 5000)
        ▲                                      ▲   │
        └──── SignalR (eventos) ───────────────┘   │ SignalR (comandos)
                                                   ▼
                                            Pcp.Agent (consola Windows)
                                                   │
                                            LibVLCSharp (kiosco)
```

- **Servidor = fuente de verdad**: persiste pantallas y comandos en MySQL 8 (producción) o SQLite (desarrollo), mantiene el mapeo
  `ScreenId ↔ ConnectionId` en memoria y retransmite telemetría/miniaturas al panel. Autentica panel y agentes con JWT.
- **Agente = ejecutor tonto**: no decide nada; obedece comandos, reporta heartbeat, miniaturas y resultado.
- **Panel = observador/operador**: pinta estado y manda comandos; no habla con agentes directamente.

---

## 2. Estructura del repositorio

```
C:\Proyectos\Pcp\
├─ AGENTS.md                  ← este archivo
├─ README.md                  ← guía de ejecución y pruebas manuales
├─ Pcp.sln / Pcp.slnx         ← solución .NET (3 proyectos)
├─ Pcp.Shared\                ← DTOs compartidos (net8.0, sin dependencias)
│  └─ Dtos\
│     ├─ ScreenInfo.cs        ← estado + telemetría + thumbnail (lo que ve el panel)
│     ├─ HeartbeatDto.cs      ← agente → servidor cada 10 s
│     ├─ ThumbnailDto.cs      ← agente → servidor cada 5 s (JPEG Base64)
│     ├─ CommandDto.cs        ← servidor → agente (CommandId, Type, PayloadJson)
│     ├─ CommandResultDto.cs  ← agente → servidor (ok/error)
│     ├─ PlayPayload.cs       ← payload del comando "play"
│     ├─ RegisterDto.cs       ← pre-registro REST del agente
│     ├─ FileDtos.cs          ← contratos del explorador de archivos (list/delete/upload)
│     ├─ GroupDtos.cs         ← grupos y respuestas de comandos masivos
│     ├─ WolDtos.cs           ← payload/resultado de Wake-on-LAN
│     ├─ TimeDtos.cs          ← hora del servidor y reproducción sincronizada
│     ├─ PlaylistDtos.cs      ← playlists, reproducción y horarios (Bloque 4)
│     ├─ RemoteControlDtos.cs ← sesiones VNC (petición de túnel, estado, respuesta REST)
│     └─ ProjectorDtos.cs     ← proyector (eventos SignalR, payload, sesión WHIP/RTSP) (Bloque 5)
├─ scripts\publish-agent.ps1  ← publish single-file + ZIP de despliegue
├─ deploy\                    ← instalador zero-touch para los sticks
│  ├─ install.bat / install.ps1 ← TightVNC + copia + tarea programada
│  ├─ uninstall.ps1
│  └─ appsettings.template.json
├─ deploy\mediamtx\           ← MediaMTX del proyector (Bloque 5) listo para cloud
│  ├─ Dockerfile              ← imagen autónoma (MediaMTX + mediamtx.yml)
│  ├─ docker-compose.yml      ← contenedor y puertos (8889/tcp, 8189/udp, 8554/tcp)
│  ├─ docker-compose.tls.yml  ← overlay opcional con Caddy (HTTPS para WHIP)
│  ├─ Caddyfile               ← proxy HTTPS → 127.0.0.1:8889
│  ├─ setup-oracle-cloud.sh   ← instalador de un clic para Oracle Free Tier
│  ├─ .env.example            ← variables del compose (auth, IP pública)
│  ├─ mediamtx.yml            ← auth delegada a /api/projector/auth y protocolos
│  └─ README.md               ← guía Oracle Free Tier, puertos y diagnóstico
├─ .env.example               ← variables de producción (servidor/agente)
├─ .config\dotnet-tools.json  ← dotnet-ef 8.0.34 fijado
├─ Pcp.Server\                ← ASP.NET Core 8 + SignalR + EF Core (MySQL prod / SQLite dev)
│  ├─ Program.cs              ← DI, proveedor BD, CORS, Swagger, migraciones, PORT, MapHub
│  ├─ Controllers\ScreensController.cs
│  ├─ Controllers\FilesController.cs  ← explorador remoto (list/delete/upload)
│  ├─ Controllers\GroupsController.cs ← CRUD de grupos y comandos masivos
│  ├─ Hubs\ScreenHub.cs
│  ├─ Models\Screen.cs, Command.cs
│  ├─ Models\ScreenGroup.cs   ← grupo lógico de pantallas
│  ├─ Data\AppDbContext.cs
│  ├─ Data\MySqlConnectionResolver.cs ← URI mysql:// y variables cloud
│  ├─ Data\AppDbContextFactory.cs     ← diseño/EF tools (fuerza MySQL)
│  ├─ Data\Migrations\                ← migración InitialCreate (MySQL)
│  ├─ Services\ScreenConnectionManager.cs, OfflineCheckerService.cs
│  ├─ Services\FileRequestManager.cs  ← correlación requestId ↔ respuesta del agente
│  ├─ Services\ScreenCommandService.cs ← envío/persistencia de comandos compartido
│  ├─ Services\WakeOnLanService.cs    ← relay de Magic Packets y envío directo opcional
│  ├─ Services\SyncPlayService.cs     ← reproducción sincronizada (instante del servidor)
│  ├─ Services\PlaylistPlaybackService.cs  ← lanza playlists en pantalla/grupo
│  ├─ Services\PlaylistSchedulerService.cs ← programador de horarios (hosted service)
│  ├─ Services\ScheduleTime.cs        ← días ISO, "HH:mm" y zona horaria
│  ├─ Services\NetworkData.cs         ← normalización de MAC/IP/CIDR y broadcast dirigido
│  ├─ Services\VncSessionManager.cs   ← sesiones VNC y puente WS panel ↔ agente
│  ├─ Controllers\ProjectorController.cs ← sesión de proyector, orden al agente y auth de MediaMTX (Bloque 5)
│  ├─ Services\ProjectorSessionManager.cs ← sesiones en memoria, tokens y autorización de MediaMTX
│  ├─ Services\ProjectorHealthService.cs ← sonda WHIP/RTSP antes de ordenar (fail-safe)
│  ├─ Services\ProjectorHealthMonitorService.cs ← cierra la emisión si MediaMTX cae
│  ├─ Controllers\TimeController.cs   ← GET /api/time (referencia de reloj para los agentes)
│  ├─ Controllers\PlaylistsController.cs  ← CRUD de playlists y reproducción
│  ├─ Controllers\SchedulesController.cs  ← CRUD de horarios y ejecución manual
│  ├─ Controllers\RemoteControlController.cs ← start/stop del escritorio remoto
│  ├─ appsettings.json        ← SQLite de desarrollo (fallback)
│  ├─ appsettings.Production.json ← proveedor MySQL y CORS por entorno
│  ├─ Properties\launchSettings.json  ← http://localhost:5000
│  └─ pcp.db (+ -shm/-wal)    ← BD SQLite local (solo desarrollo, NO versionar)
├─ Pcp.Agent\                 ← WinExe net8.0-windows (sin consola), WinForms + LibVLC
│  ├─ Program.cs              ← Host genérico, logging a archivo, instancia única y DI
│  ├─ Logging\AgentLog.cs     ← log rotativo en C:\ProgramData\PcpAgent\agent.log + ILoggerProvider
│  ├─ Services\AgentService.cs        ← SignalR, heartbeat, thumbnails, comandos, archivos
│  ├─ Services\FileService.cs         ← listar/borrar/subir con validación de rutas
│  ├─ Services\SystemCommandService.cs ← apagado, reinicio, monitor y bloqueo
│  ├─ Services\VncServerManager.cs    ← detecta/aprovisiona y arranca TightVNC local
│  ├─ Services\VncRemoteControlService.cs ← túnel WebSocket saliente hacia el servidor
│  ├─ Services\NetworkInfoProvider.cs ← detecta MAC/IP/máscara/CIDR de la NIC física (WOL)
│  ├─ Services\WakeOnLanSender.cs     ← emite Magic Packets por encargo del servidor (relay)
│  ├─ Services\ServerClock.cs         ← offset de reloj contra /api/time (Bloque 3)
│  ├─ Services\VlcPlayerManager.cs    ← ventana kiosco + LibVLC + captura + PlaySynchronized + playlists
│  └─ appsettings.json        ← opcional; las variables PCP_* tienen prioridad
└─ Pcp.Web\                   ← Next.js 14 App Router + Tailwind + SignalR client
   ├─ src\app\page.tsx        ← dashboard de pantallas
   ├─ src\app\groups\page.tsx ← sección de grupos y control masivo
   ├─ src\app\playlists\page.tsx ← playlists y programación de horarios (Bloque 4)
   ├─ src\app\layout.tsx      ← layout raíz (tema oscuro)
   ├─ src\app\globals.css
   ├─ src\components\         ← Header, SummaryBar, ScreenCard, Toast, FileExplorerModal,
   │                            GroupCard, SystemActionsMenu, RemoteDesktopModal (noVNC),
   │                            ProjectorModal (WHIP + getDisplayMedia, Bloque 5),
   │                            PlaylistEditor, PlaylistCard, ScheduleForm, ScheduleCard
   ├─ src\hooks\useScreensSignalR.ts  ← estado global + conexión SignalR
   ├─ src\services\api.ts     ← fetch REST (URL desde env)
   ├─ src\types\screen.ts     ← tipos TS espejo de los DTOs
   ├─ src\types\files.ts      ← tipos TS del explorador de archivos
   ├─ src\types\group.ts      ← tipos TS de grupos
   ├─ src\types\playlist.ts   ← tipos TS de playlists
   ├─ src\types\schedule.ts   ← tipos TS de horarios y etiquetas de días
   ├─ src\types\projector.ts  ← tipos TS de la sesión de proyector (Bloque 5)
   ├─ src\types\novnc.d.ts    ← declaraciones mínimas de @novnc/novnc
   ├─ .env / .env.local       ← desarrollo: NEXT_PUBLIC_API_URL=http://localhost:5000
   ├─ .env.production         ← build de producción: dominio real con HTTPS
   └─ package.json            ← dev en :3000; start respeta PORT de la nube
```

**No leer ni tocar**: `bin/`, `obj/`, `node_modules/`, `.next/`, `pcp.db*`.

---

## 3. Pcp.Server en detalle

### Arranque (`Program.cs`)
1. `AppDbContext` con **MySQL 8.x** (Pomelo) en producción y **SQLite** en desarrollo (fallback). Proveedor según `Database:Provider`; si no está definido, se elige MySQL cuando existe `ConnectionStrings:MySql`, `DATABASE_URL`, `MYSQL_URL` o `MYSQL_ADDON_URI`.
2. `MySqlConnectionResolver` normaliza las URI `mysql://usuario:password@host:puerto/base` de Aiven/Clever Cloud a una cadena ADO.NET (incluido `ssl-mode`).
3. `IScreenConnectionManager` singleton + `AddSignalR()` + `AddControllers()`.
4. Swagger solo en Development.
5. `OfflineCheckerService` como `HostedService`.
6. CORS configurable por `Cors:AllowedOrigins` (`Cors__AllowedOrigins__0..N`); en Development se aceptan además `localhost/127.0.0.1`. Credenciales habilitadas.
7. Inicialización: MySQL usa `Database.Migrate()` con 10 reintentos de 5 s (arranque en frío de la nube); SQLite usa `EnsureCreated()` para conservar el `pcp.db` local. **No existe `DatabaseSchemaInitializer`**.
8. Si la nube define `PORT`, Kestrel escucha en `http://0.0.0.0:{PORT}`; `UseForwardedHeaders` confía en `X-Forwarded-Proto`/`X-Forwarded-For` para HTTPS y `wss://` tras proxy.
9. Pipeline: ForwardedHeaders → CORS → `MapControllers()` → `MapHub<ScreenHub>("/hubs/screen")`.

### REST — `Controllers/ScreensController.cs` (`[Route("api/screens")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/screens` | Lista `ScreenInfo` ordenada por Id; añade `ThumbnailBase64` desde memoria. |
| GET | `/api/screens/{id}` | Detalle de una pantalla; 404 con `{ error }` si no existe. |
| POST | `/api/screens/register` | Crea pantalla (Name/Token por defecto) o actualiza `Name`. Devuelve `{ screenId, token, isNew }`. Exige `ScreenId`. |
| POST | `/api/screens/{id}/command` | Valida `type`, busca pantalla y `ConnectionId`; si no está conectada → 400. Persiste `Command` (`pending`), emite `SendCommandToAgent` al ConnectionId, marca `sent`. Devuelve `{ commandId, status, screenId, type }`. |
| POST | `/api/screens/{id}/wake` | Wake-on-LAN. Solo `Admin`. Valida MAC/IP/cableado y que esté offline; elige un agente online de la misma `NetworkCidr` y le envía el comando `wake` (modo `peer`). Con `Wol:DirectSendEnabled` emite el paquete desde el servidor si no hay relay. Devuelve `{ requested, mode, relayScreenId, macAddress, broadcastAddress, commandId, retryAfterSeconds }`. Errores: 404/400/409 (ya online)/429 (cooldown)/503 (sin relay)/500. |
| POST | `/api/screens/sync/play` | Reproducción sincronizada. Solo `Admin`. Acepta `{ screenIds[] }` o `{ groupId }`, `mediaPath`, `loop`, `volume`, `fitMode`, `startInSeconds` (3–120, por defecto 8). Fija un `StartAtUnixMs` en el reloj del servidor y envía `sync-play` a cada pantalla con el **mismo** instante. Devuelve `{ syncToken, startAtUnixMs, startAtUtc, startInSeconds, total, sent, offline, failed, results }`. Errores: 404 (grupo/pantallas inexistentes), 400 (sin destino o sin `mediaPath`). |

Reglas del comando:
- `payload` puede llegar como `JsonElement` o como objeto; se normaliza a `PayloadJson` (string JSON) antes de guardar/enviar.
- Si la pantalla figura offline pero tiene conexión activa, se corrige a `online`.
- Si el `SendAsync` falla → `Command.Status = "error"`, `ResultDetail` con la excepción, HTTP 500.
- `CommandRequest` (clase local del controlador): `Type` obligatorio, `Payload` opcional (`object`).

### REST — hora del servidor (`Controllers/TimeController.cs`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/time` | `[AllowAnonymous]`, sin caché. Devuelve `{ unixTimeMs, utcNow, server }` con la hora UTC del servidor. Lo consumen los agentes para medir su desfase de reloj antes de un `sync-play`. |

Reglas de la reproducción sincronizada (Bloque 3):
- `SyncPlayService` resuelve el destino (el grupo tiene prioridad sobre `screenIds`), limita `startInSeconds` a 3–120 s y envía a cada pantalla el comando `sync-play` con el **mismo** `StartAtUnixMs`; el orden y la latencia de los envíos no afectan a la sincronía.
- Las pantallas offline se omiten y se cuentan en `offline` (el panel lo muestra en el resumen), igual que en los comandos masivos.
- Cada agente convierte el instante del servidor a su reloj local con el offset de `ServerClock`; si la orden llega tarde arranca de inmediato y salta al punto correspondiente (`:start-time`).

### REST — `Controllers/FilesController.cs` (`[Route("api/screens/{screenId}/files")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/screens/{id}/files?path=` | Sin `path` devuelve las unidades permitidas; con `path` lista carpetas/archivos. Delegado al agente. |
| DELETE | `/api/screens/{id}/files?path=...` | Elimina **solo archivos** (nunca carpetas). |
| POST | `/api/screens/{id}/files/upload?path=...&clientRequestId=...` | Sube un archivo multipart a la carpeta indicada. Rechaza sobrescrituras. `clientRequestId` es opcional y sirve para etiquetar los eventos `FileUploadProgress` del tramo servidor → agente. |

Reglas de archivos:
- La pantalla debe existir y estar conectada; en caso contrario 404/400.
- El servidor no toca el disco del agente: `FileRequestManager` envía `FileRequest` por SignalR y espera `FileResult` con el mismo `RequestId` (timeout 30 s para listar/borrar, 5 min para subir).
- La subida se trocea en bloques de 16 KB codificados en Base64 para no superar límites de SignalR; el agente escribe a un temporal y solo lo mueve al destino al completar.
- Límite de subida: `Files:MaxUploadMb` (servidor) y `Agent:MaxUploadMb` (agente), por defecto 200 MB.
- Si el agente se desconecta, las peticiones pendientes se cancelan con error.

### REST — `Controllers/GroupsController.cs` (`[Route("api/groups")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/groups` | Lista grupos con sus `screenIds`. |
| POST | `/api/groups` | Crea grupo (`name`, `screenIds`) y asigna pantallas. |
| PUT | `/api/groups/{id}` | Renombra y reemplaza la membresía completa del grupo. |
| DELETE | `/api/groups/{id}` | Elimina el grupo y deja sus pantallas sin grupo. |
| POST | `/api/groups/{id}/command` | Envía un comando a todas las pantallas del grupo. Devuelve `{ total, sent, offline, failed, results }`. |

Reglas de grupos:
- Un grupo por pantalla (`Screen.GroupId` nullable). Asignar una pantalla a otro grupo la mueve.
- Las pantallas offline se omiten en comandos masivos y se cuentan en `offline`; no se encolan.
- `ScreenCommandService` centraliza el envío individual y masivo. La lógica es la misma que `/api/screens/{id}/command`.
- Las tablas y la FK `Screens.GroupId → ScreenGroups.Id` las crea la migración `InitialCreate` de EF Core (MySQL). No se modifican tablas manualmente.

### REST — `Controllers/PlaylistsController.cs` (`[Route("api/playlists")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/playlists` | Lista playlists con sus elementos ordenados por `position`. |
| GET | `/api/playlists/{id}` | Detalle; 404 si no existe. |
| POST | `/api/playlists` | Crea playlist (`name`, `loop`, `items[]`). Valida nombre ≤100 y rutas ≤500. |
| PUT | `/api/playlists/{id}` | Renombra y **reemplaza** los elementos por diferencias: reutiliza las filas por posición y añade/borra solo las que sobran o faltan. |
| DELETE | `/api/playlists/{id}` | Elimina la playlist; sus elementos y sus horarios caen por FK en cascada. |
| POST | `/api/playlists/{id}/play` | Lanza la playlist ahora en `screenIds[]` o `groupId`, con `startInSeconds` (3–120, 8 por defecto). Devuelve `{ playlistId, name, startAtUnixMs, startAtUtc, total, sent, offline, failed, results }`. |

Reglas de playlists:
- El panel envía la lista completa en cada guardado; `durationSeconds = 0` significa reproducir hasta el fin natural del vídeo y `volume = 0` usa el volumen por defecto de la playlist.
- El agente reproduce la lista de forma autónoma (`play-playlist`): el servidor no avanza elementos ni cronometra.
- `Loop` repite la lista desde el principio al terminar el último elemento.

### REST — `Controllers/SchedulesController.cs` (`[Route("api/schedules")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/schedules` | Horarios con playlist, destino, zona horaria y `nextRunAt` calculado. |
| GET | `/api/schedules/{id}` | Detalle. |
| POST | `/api/schedules` | Crea horario (`playlistId`, `screenId` xor `groupId`, `daysOfWeek[]` ISO 1–7, `startTime` "HH:mm", `durationMinutes`, `enabled`). |
| PUT | `/api/schedules/{id}` | Actualiza. Si cambia playlist/destino/días/hora se libera el hueco ya ejecutado; pausar o reactivar a mitad de ventana no relanza la playlist. |
| DELETE | `/api/schedules/{id}` | Elimina el horario. |
| POST | `/api/schedules/{id}/run` | Ejecuta ahora con el mismo camino que el programador (margen de 8 s). |

Reglas de programación:
- `Scheduling:TimeZone` (por defecto `Europe/Madrid`) define cómo se interpreta `startTime`; el servidor lo convierte a UTC y envía el instante absoluto (se combina con el reloj del Bloque 3).
- `PlaylistSchedulerService` revisa cada `Scheduling:TickSeconds` (20 s) y dispara `Scheduling:LeadSeconds` (10 s) antes del inicio. `LastTriggeredSlot` persiste el hueco ejecutado: un reinicio del servidor no relanza la playlist.
- Ventana de reintento: `durationMinutes` o 30 min si es 0. Si no hay pantallas online se reintenta en cada tick dentro de la ventana; las offline se omiten y se cuentan.
- Un horario sin destino (pantalla/grupo eliminados) se rechaza al editarlo y el programador lo ignora.

### REST — `Controllers/RemoteControlController.cs` (`[Route("api/screens/{screenId}/remote")]`)

| Método | Ruta | Comportamiento |
|---|---|---|
| POST | `/api/screens/{id}/remote/start` | Valida pantalla online + conexión SignalR, crea `VncSession` (contraseña RFB efímera, secreto de agente), envía `OpenVncTunnel` y espera hasta 25 s el `VncTunnelStatus`. Devuelve `{ sessionId, screenId, webSocketUrl, password, viewOnly, expiresInSeconds }`. |
| POST | `/api/screens/{id}/remote/stop` | Cierra la sesión activa de la pantalla (o la indicada en `{ sessionId }`) y avisa al agente. |

Reglas del control remoto:
- Una sola sesión por pantalla: si ya existe devuelve **409**.
- Errores: **404** pantalla inexistente, **400** offline o sin conexión, **504** el agente no confirmó, **502** el agente no pudo abrir el VNC (el `error` trae el motivo exacto).
- La sesión caduca a los **90 s** si el panel no abre el WebSocket; máximo absoluto **12 h**.
- El servidor no toca el escritorio del agente: retransmite bytes entre dos WebSockets.

### REST — Proyector / streaming (`Controllers/ProjectorController.cs`)

| Método | Ruta | Comportamiento |
|---|---|---|
| POST | `/api/screens/{id}/projector/start` | Prepara una sesión efímera y devuelve `{ sessionId, streamPath, whipUrl, whipToken, rtspUrl, hlsUrl, bufferMs, volume, expiresInSeconds }`. **No** ordena nada al agente todavía. Solo `Admin`. Errores: 404 pantalla inexistente, 400 offline, 409 emisión ya activa, 503 proyector deshabilitado. |
| POST | `/api/screens/{id}/projector/live` | Confirma que el WHIP ya publica, marca la sesión como live (hasta 12 h) y envía `OpenProjector` al agente con la URL RTSP y el respaldo HLS. Espera hasta 20 s el `ProjectorStatus`. Errores: 404 sin sesión, 400 sin conexión, 502 el agente no reproduce, 504 sin confirmación. |
| POST | `/api/screens/{id}/projector/stop` | Cierra la sesión (o la indicada en `{ sessionId }`), envía `CloseProjector` y devuelve `{ status: "closed" }`. Idempotente (204 si no hay sesión). |
| GET | `/api/screens/{id}/projector` | Estado de la sesión: `{ active, sessionId, streamPath, isLive, detail, expiresAt }`. |
| POST | `/api/projector/auth` | **Anónimo**. Lo llama MediaMTX por cada `publish`/`read`: valida la ruta `pcp/<screenId>` y el token (publicación vs lectura). Responde 200/401. Si `Projector:AuthKey` está definida, exige `?key=`. |

Reglas del proyector (Bloque 5):
- El servidor **no transporta vídeo**: solo crea tokens efímeros (192 bits, en memoria), construye URLs y coordina los eventos del Hub.
- **Fail-safe**: antes de crear una sesión (`/start`) y antes de ordenar `OpenProjector` (`/live`) se sondea MediaMTX (HTTP al WHIP + TCP al RTSP). Si no responde, se devuelve **503** `"Servidor de Streaming no disponible"`, el agente no recibe la orden y el panel muestra el error. Mientras hay emisión, `ProjectorHealthMonitorService` revisa cada 20 s y tras 2 fallos cierra la sesión (`CloseProjector` → agente a `idle`) y emite `ProjectorStatusReceived` con el motivo.
- `Projector:HealthCheckEnabled=false` desactiva todo el fail-safe (solo para desarrollo sin MediaMTX).
- `Projector:PublicWhipBaseUrl` debe ser **HTTPS** si el panel se sirve por HTTPS; de lo contrario el navegador bloquea el WHIP por contenido mixto. El despliegue recomendado (Oracle Free Tier + Caddy/DuckDNS) está en `deploy/mediamtx/README.md`.
- El panel publica con WHIP y avisa a `/live`; el agente lee RTSP con `?token=` y usa HLS como respaldo (con `Authorization: Basic` en la URL).
- La sesión caduca a los `PublisherGraceSeconds` (300 s) si el panel nunca publica, y a las `SessionMaxHours` (12 h) una vez live. Al cerrar el modal, desconectarse el agente o expirar, el servidor envía `CloseProjector`.
- La ruta se sanea (`pcp/<screenId>` → caracteres no seguros a `_`) y es única por pantalla.

### REST — Autenticación (`Controllers/AuthController.cs`)

| Método | Ruta | Comportamiento |
|---|---|---|
| POST | `/api/auth/login` | Valida usuario/contraseña (PBKDF2) y deja el JWT en la cookie HttpOnly `pcp_auth`. Límite de 10 intentos/5 min por IP. |
| POST | `/api/auth/logout` | Borra la cookie de sesión. Idempotente. |
| GET | `/api/auth/me` | Usuario autenticado (id, username, displayName, role). |
| POST | `/api/auth/agent-token` | Canjea `screenId + token de dispositivo` por un JWT con rol `Agent` y claim `screen_id` (30 días). |

Reglas de autenticación:
- Dos roles de panel: `Admin` (todo) y `Viewer` (solo lectura). Los agentes usan el rol `Agent`.
- El panel no guarda el JWT en JS: viaja en la cookie HttpOnly. El middleware de Next solo usa una cookie marcadora no sensible (`pcp_session=1`).
- Los controladores de panel usan `[Authorize(Roles = "Admin,Viewer")]`; las mutaciones exigen `Admin`. El Hub exige JWT y valida que el `screen_id` del DTO coincida con el claim.
- `POST /api/screens/register` es anónimo, pero si la pantalla ya existe exige que el token de dispositivo coincida.
- Durante una actualización escalonada, `Auth:AllowLegacyAgentTokens=true` acepta agentes antiguos que se conectan al Hub con `?screenId&token`. Los agentes nuevos envían JWT y mantienen esos parámetros solo para compatibilidad con servidores antiguos. Desactivar el flag cuando toda la flota esté actualizada.
- La clave `Auth__SigningKey` es obligatoria en producción (mínimo 32 caracteres). El administrador inicial se siembra desde `Auth__AdminUsername` / `Auth__AdminPassword` solo si `Users` está vacía.

### WebSockets del túnel — `Program.cs`

| Ruta | Quién | Comportamiento |
|---|---|---|
| `GET /ws/vnc/{sessionId}` | Panel (noVNC) | Acepta el WebSocket del navegador y lo empareja con el del agente. |
| `GET /ws/vnc-agent/{sessionId}?secret=...` | Agente | Reclama el extremo del túnel con el secreto entregado por SignalR. |

- `UseWebSockets` con `KeepAliveInterval = 20 s`; el bombeo usa buffers de 64 KB y no reencuadra mensajes (RFB es flujo puro).
- Los extremos aceptan `ws://` sin TLS sobre loopback tal cual: Kestrel no requiere ninguna opción
  extra y el esquema lo elige `RemoteControlController` (`ws` con HTTP, `wss` con HTTPS). El error
  `Sorry, loopback connections are not enabled` viene de TightVNC (`AllowLoopback`), no de Kestrel.
- Al cerrarse cualquiera de las dos puntas se cierra la otra, se elimina la sesión y se envía `CloseVncTunnel` al agente.
- **No hay autenticación de usuarios**: el `sessionId` (GUID) y el secreto son la única credencial.

### SignalR — `Hubs/ScreenHub.cs` (ruta `/hubs/screen`)

**Conexión**
- Query string `?screenId=...&token=...` → se considera **agente**.
  - Si no existe en BD, se **auto-registra** (`Pantalla {id}`, token recibido o `dev-token`, `online`).
  - Si existe, valida token: se rechaza (`Context.Abort()`) solo si el token no coincide **y** no es `dev-token`; luego marca `online` y actualiza `LastHeartbeat`.
  - Registra el mapeo en `ScreenConnectionManager` y notifica `ScreenStatusChanged(id, "online")` al grupo `panel`.
- Sin `screenId` → es el **panel web**: se une al grupo `"panel"`.
- Al desconectarse un agente: elimina mapeo, marca `offline` en BD y emite `ScreenStatusChanged(id, "offline")`.

**Métodos invocables por el agente**
| Método | Efecto |
|---|---|
| `Heartbeat(HeartbeatDto)` | Actualiza Status/LastHeartbeat/CurrentMedia/Volume/CPU/RAM/Disco en BD y emite `HeartbeatReceived(ScreenInfo)` al grupo panel. Ignora ScreenId vacío o no registrado. |
| `CommandResult(CommandResultDto)` | Actualiza `Command.Status/ExecutedAt/ResultDetail` y emite `CommandResultReceived(dto)` al panel (aunque el comando no exista en BD). |
| `SendThumbnail(ThumbnailDto)` | Guarda el último Base64 en `ConcurrentDictionary` estático (memoria, nunca en BD) y emite `ThumbnailReceived(dto)` al panel. |
| `FileResult(FileResultDto)` | Completa la petición pendiente del `FileRequestManager` correlacionada por `RequestId`. |
| `VncTunnelStatus(VncTunnelStatusDto)` | Reporta si el agente abrió el túnel VNC; resuelve la espera del endpoint `remote/start`. |
| `ProjectorStatus(ProjectorStatusDto)` | Reporta si el agente abrió o cerró la emisión; resuelve la espera de `projector/live` y retransmite `ProjectorStatusReceived` al panel. |

**Eventos servidor → panel** (nombres exactos, usados por el frontend):
`ScreenStatusChanged(screenId, status)`, `HeartbeatReceived(screenInfo)`,
`ThumbnailReceived(dto)`, `CommandResultReceived(dto)`, `FileUploadProgress(dto)`,
`ProjectorStatusReceived(dto)`.
**Evento servidor → agente**: `SendCommandToAgent(commandDto)`, `FileRequest(fileRequestDto)`,
`OpenVncTunnel(vncTunnelRequestDto)`, `CloseVncTunnel(sessionId)`,
`OpenProjector(projectorCommandPayload)` y `CloseProjector(sessionId)`.
**Evento agente → servidor**: `FileResult(fileResultDto)`, `VncTunnelStatus(statusDto)`,
`ProjectorStatus(statusDto)`.

### Servicios
- `ScreenConnectionManager`: diccionarios `ConcurrentDictionary` bidireccionales `screenId↔connectionId`. Singleton. Solo memoria: se pierde al reiniciar el servidor (los agentes reconectan).
- `OfflineCheckerService`: cada **10 s** busca pantallas `online` con `LastHeartbeat < UtcNow - 30 s`, las marca `offline`, guarda y emite `ScreenStatusChanged`.
- `VncSessionManager`: sesiones VNC en memoria (`sessionId`, `screenId`, contraseña RFB, secreto de agente, sockets panel/agente), emparejamiento de los dos WebSockets, bombeo bidireccional y limpieza. Un `Timer` cierra sesiones caducadas cada 10 s. Singleton con `IHubContext<ScreenHub>` para avisar al agente del cierre.
- `ProjectorSessionManager` (Bloque 5): sesiones de proyector en memoria con token de publicación y de lectura, ruta `pcp/<screenId>`, caducidad de gracia y máximo de 12 h. Implementa la autorización que consulta MediaMTX (`publish`/`read`) y avisa al agente con `CloseProjector` al cerrar o expirar. `Fail(...)` cierra por caída del servidor de medios y transmite el fallo al panel.
- `ProjectorHealthService` (singleton): sonda HTTP al WHIP y TCP al RTSP antes de ordenar una emisión. Cachea el resultado (`HealthCheckCacheSeconds`). Si falla, el controlador responde 503 `"Servidor de Streaming no disponible"`.
- `ProjectorHealthMonitorService` (hosted service): cada `HealthCheckIntervalSeconds` comprueba MediaMTX mientras hay sesiones live; tras `HealthCheckFailuresBeforeClose` fallos consecutivos cierra la sesión (el agente vuelve a idle) y emite `ProjectorStatusReceived` con el motivo al panel.
- `SyncPlayService` (scoped): fija el instante de arranque en el reloj del servidor, construye el `SyncPlayPayload` común y delega el envío en `ScreenCommandService`. Compartido por el endpoint `sync/play` y disponible para futuros comandos masivos sincronizados.
- `PlaylistPlaybackService` (scoped): valida la playlist (elementos no vacíos), resuelve el destino (grupo o lista de pantallas) y envía un único `play-playlist` con todos los elementos y el instante de arranque. Lo usan el panel y el programador.
- `PlaylistSchedulerService` (hosted service): cada tick busca horarios activos en su ventana horaria, evita duplicados con `LastTriggeredSlot` y lanza la playlist con el instante exacto del hueco. Respeta `Scheduling:Enabled`.
- `ScheduleTime` (static): días ISO ↔ "1,2,3", validación de "HH:mm", conversión local ↔ UTC y cálculo de `NextRunUtc`.

### Persistencia — `AppDbContext`
- `Screens` (PK `Id` string ≤50; `Name` ≤100; `Token` ≤128; `Status` ≤20; `CurrentMedia` ≤500; resto telemetría). `ThumbnailBase64` está `[NotMapped]`/`Ignore`.
  Bloque 2 (Wake-on-LAN): `MacAddress` ≤17, `LocalIp`/`SubnetMask` ≤45, `NetworkCidr` ≤64 (indexado para emparejar relays), `IsWired` bool nullable (null = agente antiguo), `WolEnabled` bool (default `true`) y `LastConnectionAt` datetime nullable.
  Bloque 3 (sincronización): `ClockOffsetMs` int nullable = desfase del reloj del agente respecto al servidor (null = aún sin sincronizar).
  Bloque 4 (playlists y programación): `Playlists` (`Id` Guid, `Name` ≤100, `Loop`, `CreatedAt`, `UpdatedAt`), `PlaylistItems` (`Id` Guid, FK `PlaylistId` en cascada, `Position`, `MediaPath` ≤500, `DurationSeconds`, `Volume`; índice `(PlaylistId, Position)`) y `Schedules` (`Id` Guid, `Name` ≤100, FK `PlaylistId` en cascada, `ScreenId`/`GroupId` con `SetNull`, `DaysOfWeek` ≤20, `StartTime` ≤5, `DurationMinutes`, `Enabled` indexado, `LastTriggeredSlot` ≤32, `LastTriggeredAt`).
- `Commands` (PK `Guid`; `ScreenId` ≤50; `Type` ≤50; `Status` ≤20; `PayloadJson`, `ExecutedAt?`, `ResultDetail` ≤1000).
- FK `Screens.GroupId → ScreenGroups.Id` con `OnDelete(SetNull)` e índice `IX_Screens_GroupId`.
- Migraciones `Data/Migrations/*` (`InitialCreate`, `AddAuthUsers`, `AddWakeOnLan`, `AddClockSync`, `AddPlaylistsScheduling`) específicas de MySQL (`char(36)`, `datetime(6)`, `utf8mb4`, `longtext`). El snapshot se usa con `dotnet ef`; SQLite de desarrollo sigue con `EnsureCreated`.

---

## 4. Pcp.Agent en detalle

### Ciclo de vida (`AgentService.ExecuteAsync`)
0. `Program.cs` (WinExe, sin consola) inicializa `AgentLog` en `C:\ProgramData\PcpAgent\agent.log`, redirige `Console.*` al archivo, registra solo el proveedor de archivo para `ILogger` y toma el mutex `Local\PcpAgent-{screenId}` (segunda instancia → termina).
1. Lee config `Agent:*` (defaults: `screen-01`, `Pantalla screen-01`, `dev-token`, `http://localhost:5000`) desde `appsettings.json` junto al `.exe` (opcional), variables estándar (`Agent__ServerUrl`), prefijo `PCP_` (`PCP_Agent__ServerUrl`) o atajos `PCP_SERVER_URL` / `PCP_SCREEN_ID` / `PCP_SCREEN_NAME` / `PCP_TOKEN`.
2. `POST /api/screens/register` hasta 3 intentos (timeout 5 s); si falla, continúa. Envía también MAC/IP/máscara/CIDR/`IsWired` de la NIC física detectada por `NetworkInfoProvider`.
2b. `ServerClock.SyncAsync()` mide el offset contra `GET /api/time` (5 muestras, se queda con la de menor RTT). El offset se revalida cada **5 min** y en cada reconexión; viaja en el heartbeat como `ClockOffsetMs`/`ClockSynced`.
3. Construye URL del Hub con `screenId` y `token` en query y `WithAutomaticReconnect` (0, 2, 5, 10, 20 s).
4. `ConnectWithRetryAsync`: reintenta cada 3 s hasta conectar.
5. Tarea independiente de miniaturas cada **5 s** (más una inmediata al conectar).
6. Bucle principal de heartbeat cada **10 s** (incluye `RemoteControlAvailable` y los datos de red para Wake-on-LAN; `NetworkInfoProvider` los refresca cada 60 s por si cambia el DHCP).
7. Escucha `OpenVncTunnel`/`CloseVncTunnel` para el escritorio remoto y `OpenProjector`/`CloseProjector` para la emisión del proyector (Bloque 5).
8. Al cancelar: `StopAsync`/`DisposeAsync` del Hub, `VncRemoteControlService.StopAllAsync()` y `VlcPlayerManager.Dispose()`.

### Publicación para el stick (`scripts/publish-agent.ps1`)
- `Pcp.Agent.csproj`: `PublishSingleFile`, `SelfContained`, `RuntimeIdentifier=win-x64`, `IncludeNativeLibrariesForSelfExtract`, `IncludeAllContentForSelfExtract`, `EnableCompressionInSingleFile` y `PublishTrimmed=false`. Resultado: `Pcp.Agent.exe` de ~216 MB, sin necesidad de instalar .NET en la mini-PC.
- Los `appsettings*.json` se excluyen del bundle y se copian junto al `.exe` para que el instalador pueda editarlos sin recompilar.
- El directorio de configuración se resuelve con `Environment.ProcessPath`, no con `AppContext.BaseDirectory`: en un single-file con extracción, `AppContext.BaseDirectory` apunta a `%TEMP%\.net\...`, mientras que el `.exe` está en la carpeta de instalación.
- LibVLC y sus plugins se extraen a un directorio temporal al arrancar; el modo degradado sigue activo si fallan. Falta validarlo en un stick real.
- Los archivos publicados no se versionan (`publish/`).
- `publish-agent.ps1` usa una salida intermedia aislada (`.build/agent/bin`) para no chocar con un agente en ejecución que bloquee `bin/Release`.
- El script copia `deploy/install.*`, `deploy/watchdog.vbs`, `deploy/appsettings.template.json` y, si existe, `deploy/tightvnc.msi`; al final genera `publish/PcpAgent.zip` (~200 MB).

### Instalación zero-touch (`deploy/`)
- `install.bat <ID-PANTALLA> [URL] [TOKEN] [-EnableAutoLogon USUARIO CONTRASENA]` solicita elevación y ejecuta `install.ps1`.
- `install.ps1` valida el ID, instala `tightvnc.msi` con `msiexec /qn /norestart`, detiene y deshabilita el servicio TightVNC (el MSI lo deja en sesión 0, incompatible con el kiosco), elimina `HKLM\Software\TightVNC\Server\ServiceOnly` en las vistas 64/32 y verifica `tvnserver.exe`.
- Copia `Pcp.Agent.exe`, `watchdog.vbs` + `appsettings.json` generado desde la plantilla a `C:\Program Files\PcpAgent` y registra la tarea `PcpAgent` con disparador `AtLogOn`, principal `Interactive` y `RunLevel Highest`. **No** usa “Run whether user is logged on or not”: esa opción ejecuta en sesión 0 y rompería LibVLC, TightVNC y el bloqueo de sesión.
- **Watchdog**: tarea `PcpAgentWatchdog` (también interactiva) que lanza `wscript.exe //B watchdog.vbs`. El script residente (sin ventana) comprueba `Pcp.Agent.exe` cada **10 s** y lo relanza si se cerró o falló; los reinicios van a `C:\ProgramData\PcpAgent\watchdog.log`. Se detiene con el flag `watchdog.stop` durante instalación/desinstalación. El Programador de tareas añade además reintento nativo (≈1 min) como red de seguridad.
- **Arranque al encender**: `-EnableAutoLogon` escribe `Winlogon\AutoAdminLogon`, `DefaultUserName`, `DefaultDomainName` y `DefaultPassword` para que Windows inicie sesión solo y dispare las tareas interactivas. Requiere usuario kiosco dedicado; la contraseña queda en texto claro en el registro (el instalador lo advierte). Sin este flag, el kiosco arranca cuando un usuario inicia sesión.
- **Instancia única**: `Program.cs` crea un mutex `Local\PcpAgent-{screenId}`; si el logon y el watchdog arrancan a la vez, la segunda instancia termina. Se conserva la simulación de varias pantallas en un mismo equipo.
- **Logs**: el agente es `WinExe` y escribe `C:\ProgramData\PcpAgent\agent.log` (rotación 5 MB × 3, configurable con `Agent:LogPath`). `install.ps1` concede Modify al grupo Usuarios en esa carpeta para que el agente, que corre sin elevar, pueda escribir.
- Configuración efectiva: `appsettings.json` < variables estándar < `PCP_*` < atajos `PCP_SERVER_URL`/`PCP_SCREEN_ID`/`PCP_TOKEN` < CLI. El arranque registra `[CONFIG] ScreenId=... ServerUrl=... Origen=... Log=...`.
- `-UseMachineEnvironment` define además `PCP_SERVER_URL`, `PCP_SCREEN_ID`, `PCP_SCREEN_NAME` y `PCP_TOKEN` a nivel de máquina para reconfiguración remota.
- Log de instalación: `C:\ProgramData\PcpAgent\install.log`; log del MSI: `tightvnc-msi.log`.
- `uninstall.ps1` detiene y elimina ambas tareas, mata el watchdog, crea/limpia el flag de parada y borra la carpeta; con `-ClearAutoLogon` deshabilita el auto-logon. No desinstala TightVNC.

### Control remoto (`VncServerManager` + `VncRemoteControlService`)
- **Detección**: busca `tvnserver.exe` en `Agent:Vnc:ExecutablePath`, `Agent:Vnc:InstallDir` (`%LOCALAPPDATA%\Pcp\vnc` por defecto), `Program Files[(x86)]\TightVNC` y, como último recurso, recursivamente en `InstallDir`. Solo se soporta **TightVNC en modo aplicación** (sin servicio).
- **Aprovisionamiento** (`Agent:Vnc:PackagePath`): `.zip` se extrae en `InstallDir` (sin admin); `.exe`/`.msi` se instalan en silencio (`/VERYSILENT` o `msiexec /qn`, requiere admin). Si no hay ejecutable ni paquete, `IsAvailable = false` con motivo accionable.
- **Arranque**: elige un puerto libre (5900-5999), cierra instancias previas de `tvnserver` de su
  sesión, escribe en `HKCU\Software\TightVNC\Server` (`RfbPort`, `Password`, `UseVncAuthentication`,
  `AcceptRfbConnections`, `AllowLoopback=1`, `LoopbackOnly=1`, `BlockRemoteInput`, `UseD3D`,
  `GrabTransparentWindows`, `AlwaysShared`, `EnableFileTransfers=0`, `AcceptHttpConnections=0`,
  `RunControlInterface=0`), lanza `tvnserver.exe -run` y espera el puerto con `TcpClient`.
  Hace *snapshot* previo del registro y lo restaura al parar. Antes de abrir el túnel valida el
  handshake RFB (banner + tipos + VNC auth) para convertir un fallo silencioso en error accionable.
- **Detalles verificados contra el código de TightVNC 2.8** (evitan repetir pruebas a ciegas):
  `tvnserver.exe` solo acepta `-run` (con `-port` muestra la ayuda y no arranca), así que el puerto
  va en `RfbPort`; `AllowLoopback` vale 0 por defecto y TightVNC rechaza entonces `127.0.0.1` con
  `Sorry, loopback connections are not enabled` (mensaje del propio TightVNC, **no** de Kestrel);
  la marca `HKLM\Software\TightVNC\Server\ServiceOnly` bloquea el modo aplicación
  (`Agent:Vnc:ForceApplicationMode`); una instancia huérfana mantiene el mutex global y el nuevo
  proceso se queda en "already running" sin abrir puerto.

- **Contraseña**: 8 caracteres generados por el servidor por sesión. En el registro se guarda como
  8 bytes: DES/ECB con la clave fija de RFB (`{23,82,107,6,35,78,88,7}`) **con los bits invertidos**
  cifrando la contraseña en claro (relleno a 8 con ceros); el cliente RFB usa como clave la
  contraseña con los bits invertidos.
- **Túnel**: `ClientWebSocket` saliente a `ws(s)://{Agent:ServerUrl}/ws/vnc-agent/{sessionId}?secret=...`; abre `TcpClient` a `127.0.0.1:{puerto}` y bombea bytes en ambos sentidos (64 KB). El agente nunca escucha en la red.
- **Kiosco**: al recibir `OpenVncTunnel` el agente llama a `IKioskWindowController.Suspend("vnc")` y en el `finally` del túnel lo reanuda (`Resume("vnc")`). Cubre cierre por orden del servidor, expiración, caída del socket y fallo al arrancar TightVNC. El vídeo puede seguir reproduciéndose minimizado.
- **Cierre**: por `CloseVncTunnel`, por caída del WebSocket o al apagar el agente → mata `tvnserver`, restaura el registro y libera el puerto.

### Comandos soportados (`ProcessCommandAsync`)
| Type | Acción | Detalle del resultado |
|---|---|---|
| `play` | Deserializa `PlayPayload`; default `C:\videos\demo.mp4`, loop `true`, volumen actual. `_playerManager.Play(...)`. Fuerza thumbnail a 1 s. | "Reproduciendo medio con LibVLC: ..." |
| `pause` | Alterna play/pausa (`MediaPlayer.Pause()` si está reproduciendo, si no `Play()`). | Pausado/reanudado |
| `stop` | `Stop()` y `CurrentMedia = "idle"`; emite thumbnail inmediata. | Detenido |
| `volume` | Lee `volume`/`Volume` del JSON (case-insensitive vía TryGetProperty de ambas variantes) y aplica clamp 0–100. | Volumen ajustado |
| `kiosk-pause` / `kiosk-resume` | Alias `pausekiosk`, `kiosk_pause`, `pause_kiosk` y `resumekiosk`, `kiosk_resume`, `resume_kiosk`. Suspende o reanuda el kiosco con el motivo `manual` (minimiza y quita `TopMost`). | Kiosco suspendido / reanudado |
| `wake` | Deserializa `WolPayload` y emite el Magic Packet (102 bytes: 6×`0xFF` + MAC×16) a `255.255.255.255:9`, al broadcast dirigido y a la última IP conocida, 3 repeticiones. | "Magic packet enviado a ... para AA:BB:..." |
| `sync-play` | Alias `syncplay`, `sync_play`. Deserializa `SyncPlayPayload`; si no hay offset de reloj lo mide primero; convierte `StartAtUnixMs` con `ServerClock.ToLocalUtc` y llama a `VlcPlayerManager.PlaySynchronized`. Programa la miniatura para después del arranque. | Programado con X ms de margen · offset del reloj · o "iniciado con N s de compensación" si llegó tarde |
| `play-playlist` | Alias `playlist`. Deserializa `PlaylistCommandPayload`, mide el reloj si hace falta y llama a `VlcPlayerManager.PlayPlaylist`. El agente avanza los elementos por fin natural o por `DurationSeconds` y repite si `Loop`. | Playlist iniciada/programada con N elementos y offset de reloj |
| `shutdown` / `restart` | **Simulados** por seguridad (no ejecutan nada). | "deshabilitado por seguridad" |
| otro | `status = "error"` | Comando no soportado |

Siempre responde con `CommandResultDto` (`ok`/`error`, `Detail`, `Timestamp`) invocando `CommandResult`.

### Proyector (`OpenProjector` / `CloseProjector`, Bloque 5)
- `OpenProjector(ProjectorCommandPayload)` no pasa por la tabla `Commands`: es un evento directo del Hub. El agente llama a `VlcPlayerManager.PlayStream` y responde `ProjectorStatus` con `Success`, `Detail` y `Protocol`.
- `PlayStream` reproduce RTSP (o HLS) con `:network-caching`/`:live-caching` = `BufferMs`, `:rtsp-tcp`, `:clock-jitter=0` y `:clock-synchro=0`; reintenta cada 1,5 s hasta que el panel publique y cambia a HLS tras 4 intentos fallidos.
- El bucle de reconexión tolera cortes y se cancela con `CloseProjector`, `play`, `play-playlist`, `sync-play`, `stop` o el cierre del agente. El `SessionId` evita que un cierre antiguo detenga una emisión nueva.
- La telemetría muestra `currentMedia = "stream: <título>"` y las miniaturas capturan el fotograma del stream igual que un vídeo normal.

### Telemetría (`SampleSystemMetrics`)
- CPU: **valor aleatorio simulado** (5–30 %) — no es medición real.
- RAM: `WorkingSet64 / GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` (aprox. del proceso, no del sistema).
- Disco: espacio libre real del drive de `AppContext.BaseDirectory`.
- `HdmiOk` siempre `true`.
- Reloj (Bloque 3): `ClockOffsetMs` = offset medido contra `/api/time` y `ClockSynced`; el panel lo muestra en la tarjeta y sirve para diagnosticar sticks con la hora mal.

### Explorador de archivos (`FileService`)
- Raíces permitidas: `Agent:FileRoots` (si está vacío, todas las unidades fijas listas).
- Lista negra: `Agent:ProtectedPaths` (por defecto `C:\Windows`, `Program Files`, `ProgramData`, `$Recycle.Bin`, `System Volume Information`, etc.).
- Toda ruta se canonicaliza con `Path.GetFullPath`, se valida contra raíces con comparación case-insensitive y separador, y se rechazan reparse points/junctions.
- `list`: devuelve raíces (ruta vacía) o hasta 2000 entradas ordenadas (carpetas primero).
- `delete`: solo archivos; rechaza carpetas.
- `upload-start/chunk/complete/cancel`: destino validado, nombre saneado (sin rutas, nombres reservados ni caracteres inválidos), sin sobrescritura, temporal oculto `.pcp-upload-{id}.tmp` y `File.Move` final.
- El agente no responde al inicio correcto de una subida; solo responde errores y la confirmación final.

### Acciones de sistema (`SystemCommandService`)
- `Agent:AllowSystemCommands` (bool, por defecto `true`) habilita las acciones destructivas.
- `Agent:SystemCommandDelaySeconds` (mínimo 5, por defecto 5) se usa para apagar/reiniciar con `shutdown.exe`.
- Comandos soportados: `shutdown` (`/s`), `restart` (`/r`), `monitor-off` (alias `monitoroff`/`monitor_off`) y `lock` (alias `lock-session`).
- Monitor: `SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)`.
- Bloqueo: `LockWorkStation()` de `user32.dll`.
- El resultado del comando se envía al servidor después de programar el apagado/reinicio; el retardo da margen a que llegue. Requiere que el agente se ejecute en la sesión interactiva del usuario.

### Reproductor (`VlcPlayerManager`)
- **Máquina de estados de la ventana** (siempre aplicada en el hilo STA con `BeginInvoke`):
  - `Kiosk` (hay medio cargado): sin bordes, maximizada, `TopMost = true`, sin cursor.
  - `Idle` (sin medio): sin bordes y maximizada según `Agent:Kiosk:IdleWindowState`, pero **nunca**
    `TopMost`: la TV sigue mostrando el standby negro sin secuestrar el escritorio.
  - `Suspended` (VNC activo u orden manual): normal/minimizada, con bordes, en la barra de tareas,
    `TopMost = false` y cursor visible.
- `IKioskWindowController` (`Suspend(reason)` / `Resume(reason)`) usa un `HashSet` de motivos
  (`"vnc"`, `"manual"`): una orden manual y una sesión de control remoto no se reanudan entre sí.
- `Agent:Kiosk:TopMostOnlyWhilePlaying` (bool, por defecto `true`) y `Agent:Kiosk:IdleWindowState`
  (`Maximized` | `Normal` | `Minimized`, por defecto `Maximized`) controlan el comportamiento.
- `Agent:DevMode` (bool): si es `true`, la ventana es normal, redimensionable, con bordes, `TopMost = false`, aparece en la barra de tareas y no oculta el cursor; si es `false` (kiosco), va maximizada, sin bordes, sin cursor y con `Escape` para alternar pantalla completa.
- Hilo STA dedicado que crea un `Form` WinForms; en modo kiosco: `FormBorderStyle.None`, maximizado, `TopMost` solo con vídeo, sin cursor, fondo negro; `Escape` alterna maximizado/normal.
- `Core.Initialize()` y `new LibVLC("--avcodec-hw=any", "--no-video-title-show", "--quiet")`; si falla, el agente sigue en **modo simulado** (sin video, heartbeat/thumbnails funcionan).
- `Play`: acepta `http/https/rtsp` como URL; cualquier otra cosa se trata como ruta local (`Path.GetFullPath`); loop vía `:input-repeat=-1`. Ya no hace `BringToFront` incondicional: si el kiosco está suspendido el vídeo sigue reproduciéndose minimizado y las miniaturas se capturan igual.
- `PlaySynchronized(mediaPath, loop, volume, startAtLocalUtc)` (Bloque 3): espera de alta resolución (Task.Delay grueso + `SpinWait` final) hasta el instante ya traducido al reloj local y arranca; si la orden llegó tarde (> 75 ms), arranca inmediato y añade `:start-time` para saltar al punto que le corresponde (no se aplica a `rtsp`, que es directo). Devuelve `SyncPlayResult` (`ScheduledStartUtc`, `StartedImmediately`, `DelayMs`, `StartOffsetSeconds`), que el agente reporta en el `CommandResult`.
- `PlayPlaylist(items, loop, volume, startAtLocalUtc?)` (Bloque 4): guarda la lista, arranca el primer elemento (o espera al instante indicado) y avanza con `EndReached` o con un `System.Threading.Timer` por `DurationSeconds`. Usa una sesión y una generación para que un temporizador viejo, un fin de vídeo simultáneo o un arranque programado anterior no muevan la lista actual. `Play`/`Stop`/`Dispose` cancelan la playlist.
- `PlayStream(url, fallbackUrl, volume, bufferMs, title, sessionId)` (Bloque 5): reproduce la emisión del proyector con caché baja, reintenta la conexión y cambia a HLS si el RTSP no arranca. `StopStream(sessionId?)` la detiene sin afectar a una emisión más nueva.
- Eventos `Playing/Paused/Stopped/EndReached/EncounteredError` mantienen `HasMedia` sincronizado (fin de vídeo sin loop → `idle` y sin `TopMost`).
- `Pause`: alterna; `Stop`: idle + stop; `SetVolume`: clamp 0–100.
- `CaptureThumbnailBase64`: si hay video, `TakeSnapshot(0, temp.jpg, 320, 180)` y devuelve `data:image/jpeg;base64,...`; si no hay video o falla, genera un JPEG sintético 320×180 de standby (fondo degradado, "PCP KIOSK", "EN REPOSO" o "PAUSADO: archivo", ID + hora, punto verde).
- Archivo temporal: `%TEMP%\pcp_snap_{guid}.jpg`, se borra tras leerlo.

---

## 5. Pcp.Web en detalle

### Vista única: `src/app/page.tsx`
- Client component que consume `useScreensSignalR()`.
- Renderiza `Header`, `SummaryBar`, aviso de error de conexión, estados de carga/vacío y grid de `ScreenCard` (1/2/3 columnas).
- `ToastContainer` para notificaciones flotantes (auto-cierre 4 s).

### Componentes
- `Header.tsx`: logo, badge de estado SignalR (`connected`/`reconnecting`/`disconnected`) y botón Refrescar.
- `SummaryBar.tsx`: total, online, offline y "en reproducción" (online y `currentMedia !== "idle"`).
- `ScreenCard.tsx`: monitor 16:9 con thumbnail en vivo (badge EN VIVO/STANDBY), efecto "SIN SEÑAL" con barras SMPTE si offline, nombre/ID, MAC e IP (mono, solo si el agente las reportó), badge online, medio actual, CPU/RAM/Disco/Latido, botones Play/Pausar/Stop y panel "Configurar medio / volumen" (ruta local o URL + slider 0–100).
  - Si la pantalla está **offline** aparece **Encender (Wake-on-LAN)**: pide `POST /wake`, muestra cuenta atrás de 45 s y se limpia sola cuando llega el `ScreenStatusChanged` a online. Se deshabilita sin MAC, con `wolEnabled = false` o si `isWired = false` (Wi-Fi), con tooltip explicativo.
  - Panel "Configurar medio / volumen": incluye **Sincronizar Play** (Bloque 3): selector de margen 5/8/10/15/30 s, botón que llama a `syncPlay` y muestra el desfase de reloj del agente (`clockOffsetMs`) junto al título.
  - `Play` envía `{ mediaPath, loop: true, volume, fitMode: "cover" }`.
  - `Pausar` → `pause`; `Stop` → `stop`; "Aplicar volumen" → `{ volume }`.
  - Ruta por defecto en el input: `C:\videos\promo_verano.mp4`.
- `Toast.tsx`: contenedor de alertas.
- `FileExplorerModal.tsx`: modal del explorador remoto. Muestra unidades, breadcrumb, listado de carpetas/archivos, tamaño/fecha, subida múltiple a la carpeta actual, borrado con confirmación y botón "Reproducir" para videos (reutiliza el comando `play`). Incluye **drag & drop** sobre el listado (rechaza carpetas y exige una carpeta abierta) y una barra de progreso real en dos fases: `Enviando al servidor` (XHR) y `Entregando al agente` (eventos SignalR), con velocidad, ETA y botón Cancelar. Se abre desde el botón "Archivos" de `ScreenCard` (solo si la pantalla está online).
- `SystemActionsMenu.tsx`: menú "Sistema" de cada tarjeta con apagar PC, reiniciar, apagar monitor y bloquear sesión; confirmación para acciones destructivas.
- `src/app/groups/page.tsx`: creación de grupos, selección de pantallas y listado.
- `GroupCard.tsx`: edición de nombre/membresía, reproducción masiva con ruta/volumen, apagado y reinicio masivos. Incluye **Sincronizar** (ruta/volumen/margen → `POST /api/screens/sync/play` con `groupId`) y **Encender apagadas (N)**: recorre en serie solo las pantallas offline, cableadas, con MAC y WOL habilitado, y muestra un resumen por pantalla (relay usado o error) más el recuento de omitidas.
- `RemoteDesktopModal.tsx`: modal a pantalla completa con noVNC (`@novnc/novnc/lib/rfb`, import dinámico). Pide sesión a `startRemoteSession`, muestra estado (preparando/conectando/conectado/error), permite *Solo ver*, `Ctrl+Alt+Del`, pantalla completa y reintento; al cerrar desconecta el RFB y llama a `stopRemoteSession`.
- `ProjectorModal.tsx` (Bloque 5): captura la pantalla del operador con `navigator.mediaDevices.getDisplayMedia` (calidad y audio configurables) y la publica a MediaMTX por **WHIP** (RTCPeerConnection + POST SDP, ICE no-trickle). Al conectar llama a `notifyProjectorLive` para que el agente reproduzca el stream; muestra vista previa, cronómetro, bitrate real (`getStats`), aviso de contenido mixto y al cerrar hace `DELETE` del recurso WHIP + `stopProjectorSession`.
- `ScreenCard.tsx`: botón **Control Total** (solo online) bajo Archivos/Sistema; abre `RemoteDesktopModal`. Debajo, botón **Proyector (compartir pantalla)** que abre `ProjectorModal`.
- `Header.tsx`: navegación entre `/`, `/groups` y `/playlists`.
- `src/app/playlists/page.tsx` (Bloque 4): sección de playlists (editor + tarjetas con reproducción inmediata) y sección de programación (formulario y tarjetas de horarios). Carga `fetchPlaylists` + `fetchSchedules` y, en paralelo, `fetchGroups` para los selectores de destino.
- `PlaylistEditor.tsx`: nombre, bucle y lista ordenada de elementos (añadir, subir/bajar, quitar, duración en segundos y volumen). Reemplaza la lista completa al guardar.
- `PlaylistCard.tsx`: resumen de elementos y duración planificada, selector de destino (grupo o pantalla) y botón **Reproducir** (`playPlaylist` con 8 s de margen), edición y borrado. En modo Viewer solo muestra la lista.
- `ScheduleForm.tsx`: playlist, destino, días ISO, hora local, ventana en minutos y activo. Valida antes de enviar.
- `ScheduleCard.tsx`: días humanizados (`L–V`, fin de semana, todos), hora, próxima ejecución, última ejecución, zona horaria y acciones **Pausar/Activar**, **Ejecutar ahora**, editar y eliminar.

### Estado y comunicación — `src/hooks/useScreensSignalR.ts`
- Carga inicial `GET /api/screens`.
- Conexión SignalR a `${SERVER_URL}/hubs/screen` (WebSockets + LongPolling, credenciales, reconexión 0/2/5 s).
- Suscribe: `HeartbeatReceived` (merge por `id`, fuerza `status: "online"`, añade si no existe), `ScreenStatusChanged`, `ThumbnailReceived` (actualiza `thumbnailBase64`), `CommandResultReceived` (toast éxito/error).
- Tras reconectar vuelve a cargar la lista por REST.
- Expone: `screens, loading, error, connectionStatus, toasts, addToast, removeToast, refreshScreens`.

### REST — `src/services/api.ts`
- `SERVER_URL` = `NEXT_PUBLIC_API_URL` → `NEXT_PUBLIC_SERVER_URL` → `http://localhost:5000`, sin barra final. `NEXT_PUBLIC_*` se incrusta en **build**; en producción debe venir de `.env.production` o del panel de la nube.
- `fetchScreens()`: `GET /api/screens`, `cache: "no-store"`.
- `sendScreenCommand(id, { type, payload })`: `POST /api/screens/{id}/command`; propaga `data.error` del servidor.
- `wakeScreen(id, relayScreenId?)`: `POST /api/screens/{id}/wake`; devuelve `{ mode, relayScreenId, macAddress, ... }`. El panel usa `mode` para el mensaje del toast.
- `syncPlay(request)`: `POST /api/screens/sync/play`; acepta `screenIds` o `groupId` y devuelve el instante de arranque (`startAtUtc`, epoch) y el resumen por pantalla.
- `fetchPlaylists`, `createPlaylist`, `updatePlaylist`, `deletePlaylist` y `playPlaylist(id, request)`: CRUD y reproducción inmediata (Bloque 4).
- `fetchSchedules`, `createSchedule`, `updateSchedule`, `deleteSchedule` y `runSchedule(id)`: CRUD y ejecución manual de horarios.
- `fetchFiles(screenId, path?)`, `deleteFile(screenId, path)` y `uploadFile(screenId, directoryPath, file)`: consumen `/api/screens/{id}/files`.
- `uploadFileWithProgress(screenId, directoryPath, file, { onBrowserProgress, signal })`: sube con `XMLHttpRequest` (progreso real navegador → servidor, cancelable con `AbortController`) y devuelve `{ clientRequestId, promise }`. El tramo servidor → agente se sigue con `subscribeUploadProgress(clientRequestId, cb)` (evento SignalR `FileUploadProgress`). `uploadFile` se mantiene como envoltorio sin progreso.
- `startRemoteSession(screenId, viewOnly)` y `stopRemoteSession(screenId, sessionId?)`: consumen `/api/screens/{id}/remote/{start|stop}`.
- `startProjectorSession`, `notifyProjectorLive` y `stopProjectorSession` (Bloque 5): preparan la sesión WHIP, confirman la publicación y cierran la emisión en `/api/screens/{id}/projector/*`.
- `next.config.mjs` usa `output: "standalone"` y `npm start` respeta el `PORT` de la nube. `NEXT_PUBLIC_API_URL` es de build-time: si falta en producción, el panel cae a `http://localhost:5000` y registra un error en consola.

### Tipos — `src/types/screen.ts`
Espejo camelCase de los DTOs: `ScreenInfo` (incluye `remoteControlAvailable`, `macAddress`, `localIp`, `wolEnabled`, `isWired`, `clockOffsetMs`, `clockSynced`), `RemoteSession`, `ThumbnailDto`, `CommandRequest`, `CommandResponse`, `CommandResultDto`, `SyncPlayRequest`, `SyncPlayResponse`, `ToastMessage`.
`src/types/playlist.ts` (`Playlist`, `PlaylistItem`, `PlaylistUpsert`, `PlaylistPlayResponse`) y `src/types/schedule.ts` (`Schedule`, `ScheduleUpsert`, `DAY_LABELS`, `parseDays`, `describeDays`).
`src/types/projector.ts` (`ProjectorSession`, `ProjectorActionResponse`, `ProjectorStatusResponse`, `ProjectorStatusDto`).
`src/types/novnc.d.ts` declara la superficie mínima de `@novnc/novnc`.
**Si cambia un DTO en `Pcp.Shared`, actualizar este archivo.**

---

## 6. Reglas de negocio actuales (resumen operativo)

1. **Identificación**: `ScreenId` es la clave de todo (BD, mapeo de conexión, eventos). El token es solo defensa básica; `dev-token` se acepta siempre.
2. **Online/Offline**: online al conectar o al recibir heartbeat; offline al desconectar o tras **30 s** sin heartbeat (chequeo cada 10 s). El panel refleja el cambio por SignalR.
3. **Comandos**: solo se envían a pantallas con conexión activa; quedan auditados en `Commands` con ciclo `pending → sent → ok|error`. El resultado llega al panel como toast.
4. **Reproducción**: un solo medio a la vez por pantalla; `stop` vuelve a `idle`; `pause` es *toggle* (no pausa idempotente).
5. **Miniaturas**: el agente las produce cada 5 s (o inmediatamente tras play/stop); el servidor solo guarda **la última por pantalla en memoria** (no hay histórico ni persistencia). El panel las pinta en Base64.
6. **Métricas**: CPU simulada, RAM aproximada del proceso, disco real. No usar estos valores para alertas de producción.
7. **Seguridad**: panel y agentes se autentican con JWT (cookie HttpOnly para el panel, `Authorization: Bearer` para el agente y `access_token` en WebSockets). Roles `Admin`/`Viewer`; las mutaciones exigen `Admin`. CORS limitado a `Cors:AllowedOrigins` y validación de `Origin` en métodos mutantes como defensa CSRF. `shutdown`/`restart` siguen requiriendo `AllowSystemCommands` en el agente. El auto-logon opcional del instalador guarda la contraseña en texto claro en el registro: usar un usuario kiosco dedicado y sin privilegios de administrador.
8. **Persistencia**: MySQL 8 con migraciones EF (`Database.Migrate()`); SQLite es solo fallback de desarrollo con `EnsureCreated()`. Thumbnails y mapeo de conexiones viven solo en memoria. La tabla `Users` guarda usuarios del panel con hash PBKDF2 y rol. En desarrollo, al añadir tablas hay que recrear `pcp.db` (EnsureCreated no altera una BD existente).
9. **Sin tests automatizados** en la solución.
10. **Archivos**: el explorador navega por las unidades fijas del agente con lista negra de rutas críticas; solo borra archivos, no carpetas; no sobrescribe archivos existentes; la subida va por chunks Base64 y se confirma en un archivo temporal movido al destino. El panel muestra progreso real: XHR para el tramo navegador → servidor y `FileUploadProgress` (SignalR, identificado por `clientRequestId`) para el tramo servidor → agente. "Reproducir" no descarga el archivo: envía `play` con la ruta local del agente.
11. **Transferencia de archivos**: el servidor actúa de puente; no almacena los archivos subidos. Si el agente se cae a mitad de una subida, el servidor cancela y el agente limpia el temporal al cancelar; si el proceso muere, puede quedar un `.pcp-upload-*.tmp`.
12. **Grupos**: un grupo por pantalla; eliminar un grupo solo desasigna pantallas. Los comandos masivos omiten pantallas offline y devuelven el resumen de envíos.
13. **Comandos de sistema**: `shutdown`, `restart`, `monitor-off` y `lock` son reales en el agente. Están protegidos por `Agent:AllowSystemCommands`, usan 5 s de retardo para apagar/reiniciar y el panel pide confirmación. El endpoint exige JWT con rol `Admin` (cookie del panel).
14. **Control remoto**: una sesión VNC por pantalla; el agente solo expone el RFB en `127.0.0.1` y sale por WebSocket al servidor. La contraseña RFB es aleatoria de 8 caracteres y vive solo lo que dura la sesión; el servidor la devuelve al panel porque el handshake RFB la exige.
15. **Caducidad de sesión**: 90 s para que el panel abra el WebSocket; luego indefinida hasta 12 h. Cerrar el modal, cerrar el WebSocket, desconectar el agente o reiniciar el servidor cierra el túnel y restaura la configuración de TightVNC.
16. **Dependencia externa**: TightVNC debe existir en la mini-PC (o aprovisionarse desde `Agent:Vnc:PackagePath`). El agente debe correr en la sesión interactiva; un servicio de TightVNC activo impide el modo aplicación y se reporta como error al panel.
17. **Rendimiento**: noVNC usa `scaleViewport`, `qualityLevel≈6` y `compressionLevel≈1`; TightVNC se configura con captura D3D y sin transferencia de archivos. En LAN da ~30–60 fps a 1080p. No se envía audio, portapapeles ni archivos por este canal.
18. **Wake-on-LAN**: un Magic Packet no cruza Internet, y el servidor vive en Render, así que el paquete lo emite **otro agente online de la misma `NetworkCidr`** (relay) al recibir el comando `wake`; el envío directo desde el servidor es opcional (`Wol:DirectSendEnabled`, solo útil on-prem). Si no hay relay disponible no se puede despertar. El endpoint exige `Admin`, aplica cooldown (`Wol:CooldownSeconds`) y audita el `wake` en `Commands`. Por Wi-Fi el WOL no funciona: el panel deshabilita el botón cuando `IsWired = false`. El encendido masivo del grupo se hace en serie desde el panel reutilizando el endpoint individual; cada pantalla cuenta con su propio cooldown y su propio resultado.
19. **Sincronización de reproducción**: el servidor fija `StartAtUnixMs` en su reloj y todos los agentes reciben el mismo valor; cada uno lo traduce a su reloj con el offset de `/api/time`, así que un stick con la hora desviada arranca igual. El margen mínimo es 3 s (por defecto 8) para absorber la latencia de los comandos. Si la orden llega después del instante, el agente arranca y salta al punto correspondiente para mantener el fotograma alineado. El panel muestra el `startAtUtc` y el resumen de envíos; las pantallas offline se omiten.
20. **Diagnóstico de reloj**: el offset (`servidor - agente`) viaja en cada heartbeat y se persiste en `Screens.ClockOffsetMs`. Un valor grande (> 1–2 s) explica miniaturas con horas raras y sirve para decidir si hay que sincronizar la hora del stick con NTP.
21. **Playlists**: una lista ordenada de medios con duración y volumen por elemento. El agente reproduce la secuencia por su cuenta y el servidor no interviene entre elementos; por eso una playlist sobrevive a la caída del servidor, pero no a un reinicio del agente (queda en `idle` hasta la siguiente orden u horario). `stop`/`play` manuales cancelan la playlist en curso.
22. **Programación**: los horarios se evalúan en el servidor con la zona `Scheduling:TimeZone` y se disparan con 10 s de antelación; el instante de arranque que reciben los agentes es absoluto y pasa por el reloj del Bloque 3, por lo que varias pantallas de un grupo arrancan la playlist a la vez. Solo hay una instancia del programador (si se escala el servidor a varias réplicas, cada una intentaría ejecutar el horario; requiere coordinación distribuida).
23. **Proyector**: el panel publica su pantalla por WHIP a MediaMTX y el agente la reproduce por RTSP con caché baja (HLS como respaldo). El servidor solo emite tokens efímeros y autoriza las rutas `pcp/<screenId>`; **no** transporta vídeo. Una emisión por pantalla; al cerrar el modal, caer el agente o caducar la sesión se envía `CloseProjector`. Requiere MediaMTX accesible desde el navegador (HTTPS si el panel es HTTPS) y desde la LAN de los sticks.
24. **Permisos del proyector**: crear/cerrar sesiones y publicar exige `Admin`; un `Viewer` no ve el botón. Los tokens de publicación y lectura son distintos: un token de lectura no permite publicar en la ruta.

---

## 7. Configuración y ejecución

```powershell
# Servidor en desarrollo (SQLite, http://localhost:5000, Swagger en /swagger)
cd C:\Proyectos\Pcp\Pcp.Server; dotnet run

# Servidor en producción local contra MySQL (lee .env.example como referencia)
$env:ASPNETCORE_ENVIRONMENT="Production"
$env:DATABASE_URL="mysql://usuario:password@host:3306/pcp?ssl-mode=REQUIRED"
$env:Cors__AllowedOrigins__0="https://panel.example.com"
cd C:\Proyectos\Pcp\Pcp.Server; dotnet run

# Panel web
cd C:\Proyectos\Pcp\Pcp.Web; npm run dev          # :3000
# build de producción (lee .env.production)
npm run build; npm start                          # respeta PORT

# Agente en desarrollo
cd C:\Proyectos\Pcp\Pcp.Agent; dotnet run

# Agente instalable para el stick (single-file self-contained, win-x64)
powershell -ExecutionPolicy Bypass -File C:\Proyectos\Pcp\scripts\publish-agent.ps1
```

Claves de configuración relevantes:
- `Pcp.Server/appsettings.json` → `Database:MySqlVersion`, `ConnectionStrings:DefaultConnection` (SQLite dev), `Files:MaxUploadMb`, `Wol:*` (`Enabled`, `CooldownSeconds`, `Port`, `DirectSendEnabled`, `PreferredRelayScreenId`), `Scheduling:*` (`Enabled`, `TimeZone`, `TickSeconds`, `LeadSeconds`), `Projector:*` (`Enabled`, `PathPrefix`, `PublicWhipBaseUrl`, `PublicHlsBaseUrl`, `PublicRtspUrl`, `ReadUser`, `AuthKey`, `PublisherGraceSeconds`, `SessionMaxHours`, `BufferMs`, `Volume`, `HealthCheckEnabled`, `HealthCheckTimeoutSeconds`, `HealthCheckCacheSeconds`, `HealthCheckRtsp`, `HealthCheckIntervalSeconds`, `HealthCheckFailuresBeforeClose`, `HealthCheckUrl`), `Auth:*` (emisor, audiencia, cookie, duración de tokens, usuario admin inicial).
- `Pcp.Server/appsettings.Production.json` → `Database:Provider=MySql`, `Cors:AllowedOrigins`, `Auth:CookieSameSite=None`. La cadena real y `Auth__SigningKey` **nunca** se versionan: usar `ConnectionStrings__MySql` o `DATABASE_URL`/`MYSQL_URL`/`MYSQL_ADDON_URI`, más `Auth__SigningKey`, `Auth__AdminPassword`, `Cors__AllowedOrigins__0..N`, `PORT` y `Files__MaxUploadMb`.
- `Pcp.Agent/appsettings.json` (opcional) → `Agent:ScreenId`, `Agent:Name`, `Agent:Token`, `Agent:ServerUrl`, `Agent:DevMode`, `Agent:AllowSystemCommands`, `Agent:SystemCommandDelaySeconds`, `Agent:FileRoots`, `Agent:ProtectedPaths`, `Agent:MaxUploadMb`, `Agent:LogPath`, `Agent:Kiosk:TopMostOnlyWhilePlaying`, `Agent:Kiosk:IdleWindowState`, `Agent:Vnc:*`, `Agent:Wol:InterfaceName` y `Agent:Wol:MacOverride` (overrides manuales de la NIC para WOL; el override por nombre selecciona esa interfaz aunque parezca virtual). Se puede omitir y usar `PCP_SERVER_URL`, `PCP_SCREEN_ID`, `PCP_SCREEN_NAME`, `PCP_TOKEN` o `PCP_Agent__*`.
- `Pcp.Web/.env.local` (dev) y `Pcp.Web/.env.production` (build) → `NEXT_PUBLIC_API_URL` (y `NEXT_PUBLIC_SERVER_URL`).
- `.env.example` en la raíz documenta todas las variables de producción.

Migraciones MySQL (herramienta fijada en `.config/dotnet-tools.json`):

```powershell
cd C:\Proyectos\Pcp
dotnet tool restore
dotnet ef migrations add <Nombre> --project Pcp.Server\Pcp.Server.csproj --startup-project Pcp.Server\Pcp.Server.csproj --configuration Release --output-dir Data\Migrations
dotnet ef database update --project Pcp.Server\Pcp.Server.csproj --startup-project Pcp.Server\Pcp.Server.csproj --configuration Release
```

`AppDbContextFactory` fuerza MySQL en tiempo de diseño; no hace falta una base local para generar migraciones.

Para simular varias pantallas: ejecutar varios agentes con distinto `PCP_SCREEN_ID`
o con `Agent:ScreenId` (appsettings/CLI).

---

## 8. Huecos conocidos / deuda técnica

- No existe descarga de archivos desde el panel ni renombrado/movimiento; el explorador solo lista, sube, borra y reproduce videos.
- Los endpoints de archivos y grupos usan el JWT del panel (Admin/Viewer), pero no hay permisos por pantalla: cualquier `Admin` puede operar toda la flota.
- No hay endpoints para consultar historial de comandos ni CRUD completo de pantallas (solo register/list/detail/command y grupos).
- No hay HTTPS propio: el panel y la API dependen del proxy de la nube (Render/Vercel). El rate limiting solo cubre el login (10 intentos/5 min por IP).
- No hay tests automatizados. Las migraciones EF son específicas de MySQL (`InitialCreate`, `AddAuthUsers`, `AddWakeOnLan`, `AddClockSync`); SQLite de desarrollo usa `EnsureCreated` y no aplica esas migraciones.
- Gestión de usuarios incompleta: no hay CRUD de `Users` ni cambio de contraseña desde el panel; los usuarios adicionales deben insertarse en BD con el hash PBKDF2. El token de dispositivo por defecto `dev-token` sigue aceptándose para no romper la flota y se registra una advertencia en producción; debe rotarse antes de exponer el sistema.
- El JWT del agente dura 30 días y se renueva solo al reiniciar o al reconectar SignalR; no hay endpoint de refresh explícito.
- `fitMode` se envía pero **no se aplica** en el agente.
- `pause` no distingue pausar de reanudar; no hay `seek`.
- Thumbnails sin límite de tamaño/antigüedad y sin compresión adaptativa.
- El panel no muestra el estado del comando `pending/sent` (solo el resultado final).
- El puerto del panel difiere entre `package.json` (3000) y README (3001).
- El control remoto no tiene autenticación de usuario ni TLS: el `sessionId`/secreto son la única credencial y la contraseña RFB viaja en la respuesta REST. Requiere HTTPS + login antes de producción.
- Solo se soporta TightVNC; UltraVNC/RealVNC se detectan como no disponibles hasta implementar su configuración.
- El canal VNC no reenvía audio ni permite transferencia de archivos (a propósito); no hay métricas de latencia en el modal.
- Si el agente corre como servicio (sesión 0) o queda un `tvnserver` huérfano, el control remoto falla con error explícito; no hay reintento automático de arranque del VNC.
- El agente es `WinExe`: no hay consola y el diagnóstico exige leer `C:\ProgramData\PcpAgent\agent.log` (o `%LOCALAPPDATA%\Pcp\logs\agent.log` si no hay permisos).
- El watchdog comprueba el proceso por nombre (`Pcp.Agent.exe`): asume una pantalla por stick. En desarrollo con varios agentes en el mismo equipo solo vigila que exista al menos uno.
- **Sincronización**: el offset se mide contra la hora del servidor, pero si un stick tiene el reloj muy desviado el margen de espera puede no bastar (se compensa saltando en el medio, no se corrige el reloj del sistema). No hay verificación posterior de que todos los agentes arrancaron en el mismo fotograma ni métrica de desfase real entre pantallas; el `CommandResult` solo informa el margen y el salto aplicado.
- La sincronización no controla el audio de forma especial ni resincroniza si un agente se reconecta a mitad de la reproducción; `sync-play` siempre reinicia el medio desde el punto calculado.
- El encendido masivo por grupo no tiene endpoint propio: el panel itera las pantallas en serie, así que un grupo grande tarda y no hay resumen persistido en el servidor (solo los `Commands` auditados de cada `wake`).
- **Playlists**: no hay transiciones (fade), ni subtítulos, ni audio independiente por elemento; la duración por elemento es opcional y, si es 0, dependen del fin natural del vídeo, por lo que pantallas con códecs/tiempos distintos pueden desincronizarse a partir del segundo elemento. El agente no persiste la playlist en disco: si se reinicia, no reanuda sola.
- **Programación**: el programador es un `BackgroundService` de una sola instancia y no hay calendario de excepciones (festivos, vacaciones) ni prioridad entre horarios solapados: si dos coinciden, se lanzan ambos y gana el último comando. No se recuperan ejecuciones perdidas si el servidor está caído durante toda la ventana. La zona horaria es global (`Scheduling:TimeZone`), no por horario.
- **Proyector**: depende de MediaMTX (no se instala desde el panel) y de que el navegador del operador conceda `getDisplayMedia`; si el panel es HTTPS, MediaMTX debe ser HTTPS o el navegador bloquea el WHIP. La sesión del proyector vive en memoria del servidor: un reinicio corta la coordinación (el agente deja de recibir el cierre y el stream caduca por MediaMTX). No hay audio bidireccional, grabación ni selección de pista; el audio del sistema solo llega si el operador lo marca y el navegador lo permite. `Projector:AuthKey` protege el endpoint de autorización, pero MediaMTX debe poder alcanzar `Pcp.Server` (por eso `host.docker.internal` en el compose de desarrollo). En cloud, el despliegue zero-cost está en `deploy/mediamtx` (Oracle Free Tier + `setup-oracle-cloud.sh`) y la sonda de salud cierra la emisión si MediaMTX deja de responder.
- **Proyector (alcance del fail-safe)**: la sonda comprueba MediaMTX **desde el servidor** (WHIP HTTP + RTSP TCP), no el camino real hasta cada stick. Un firewall que bloquee 8554 solo para la LAN del agente, o una pérdida de paquetes UDP, no se detecta; en ese caso el agente agota sus reintentos. Tampoco se consulta el path concreto en la API de MediaMTX (el 9997 no se expone), solo la disponibilidad global.

---

## 9. Convenciones para agentes de código

- Idioma: comentarios, logs y UI en **español**; nombres de código en inglés.
- Respetar la separación: DTOs en `Pcp.Shared`, lógica de dominio en `Pcp.Server`, hardware en `Pcp.Agent`, presentación en `Pcp.Web`.
- Cualquier contrato nuevo (DTO/evento/endpoint) debe actualizarse en los tres lados afectados: Shared, Server, Web (`types/screen.ts`).
- Mantener los nombres exactos de eventos SignalR existentes; el frontend depende de ellos.
- No romper el modo degradado del agente cuando LibVLC no está disponible.
- Antes de modificar esquema en producción: crear una migración EF y aplicarla con `dotnet ef database update` (o dejar que el arranque ejecute `Migrate()`). `EnsureCreated` solo afecta a SQLite de desarrollo y no actualiza tablas existentes.
