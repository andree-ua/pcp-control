# PCP — Panel de Control de Pantallas
## Fase 0 (Backend & Agente) + Fase 1 (Panel Web Next.js) + Fase 2 (Reproductor LibVLC & Miniaturas) + Fase 3 (Control Total VNC, JWT y roles)

Sistema de administración y control remoto para mini-PCs Windows (sticks) conectadas a televisores, administradas centralizadamente mediante ASP.NET Core, SignalR, Entity Framework Core (MySQL 8 en producción, SQLite en desarrollo), reproductor multimedia kiosco acelerado por hardware (LibVLC) y panel de control en Next.js con streaming de miniaturas en vivo.

---

## 🏗 Arquitectura del Sistema

```
┌─────────────────────────────────────────────────────────────┐
│                 Pcp.Web (Panel de Control)                  │
│  Next.js 14 · React 18 · TypeScript · Tailwind CSS          │
│  Monitor TV con streaming de fotogramas (Base64 JPEG)       │
│  Cliente SignalR (@microsoft/signalr) · Puerto 3001 / 3000  │
└──────────────┬───────────────────────────▲──────────────────┘
               │ HTTP REST                 │ SignalR WebSocket
               │ (Comandos / Screens)      │ (Telemetría + Thumbnails 5s)
               ▼                           │
┌──────────────────────────────────────────┴──────────────────┐
│                         Pcp.Server                          │
│  ASP.NET Core 8 · SignalR Hub (/hubs/screen)                │
│  EF Core (MySQL 8 prod / SQLite dev) · API REST             │
│  CORS configurable por entorno                              │
│  Puerto: PORT de la nube o http://localhost:5000            │
└────────────────┬───────────────────────────▲────────────────┘
                 │                           │
  Comandos       │                           │ Heartbeat (10s)
  (play, stop..) │                           │ Thumbnails en vivo (5s)
                 ▼                           │
┌────────────────────────────────────────────┴────────────────┐
│                         Pcp.Agent                           │
│  Consola .NET 8 (Windows) · LibVLCSharp + LibVLC nativo     │
│  Ventana Kiosco: Pantalla completa, sin bordes, sin cursor   │
│  Aceleración por Hardware (D3D11 / DXVA2 / Directx-hw-yuv)  │
│  Captura de fotogramas (320x180 px) y standby sintético     │
└─────────────────────────────────────────────────────────────┘
```

---

## 📦 Nuevos Paquetes y Tecnologías (Fase 2)

- **`LibVLCSharp` & `LibVLCSharp.WinForms` (3.10.2)**: Integración para .NET con motor de reproducción nativo VLC.
- **`VideoLAN.LibVLC.Windows` (3.0.24)**: Binarios y códecs nativos de VLC empaquetados para Windows x64/x86 con soporte de aceleración por hardware GPU (ideal para mini-PCs/sticks de bajo consumo).
- **Ventana Kiosco**: Hilo STA con ventana `FormBorderStyle.None`, maximizada a pantalla completa, `TopMost = true` y cursor oculto (`Cursor.Hide()`). Permite salir o alternar con la tecla `Escape` durante pruebas de desarrollo.
- **Transmisión de Miniaturas (Thumbnails)**: El agente genera o captura un fotograma cada 5 segundos (resolución optimizada 320x180 px) en formato JPEG Base64 y lo transmite al Hub de SignalR, retransmitiéndolo en tiempo real a los clientes del panel web. Si el reproductor está en reposo o detenido, se genera un fotograma de standby de diseño limpio.

---

## 🚀 Guía de Ejecución en Local (Paso a Paso)

Abre **3 terminales** de PowerShell:

### 1. Terminal 1: Servidor Backend (.NET 8)

```powershell
cd C:\Proyectos\Pcp\Pcp.Server
dotnet run
```
*Servidor operativo en:* `http://localhost:5000`  
*Swagger interactivo:* `http://localhost:5000/swagger`

En desarrollo se siembra el administrador `admin` / `admin123` si la tabla `Users`
está vacía (configurable con `Auth__AdminUsername` / `Auth__AdminPassword`).
En producción esas variables son obligatorias para el primer arranque.

---

### 2. Terminal 2: Panel Web (Next.js)

```powershell
cd C:\Proyectos\Pcp\Pcp.Web
npm run dev -- -p 3001
```
*(O `npm run dev` para el puerto 3000 por defecto)*  
*Abre en tu navegador:* **[http://localhost:3001](http://localhost:3001)**

---

### 3. Terminal 3: Agente de Pantalla (`screen-01`)

```powershell
cd C:\Proyectos\Pcp\Pcp.Agent
dotnet run
```
*Comportamiento inmediato:*
- Se inicializa el motor de LibVLC con aceleración por hardware.
- Se crea la ventana kiosco a pantalla completa en color negro y cursor oculto.
- Se conecta al Hub en `http://localhost:5000/hubs/screen`.
- Comienza a emitir latidos cada 10s y fotogramas cada 5s.
- En el panel web, la tarjeta mostrará el monitor de TV en vivo con el estado **STANDBY**.

---

## 🎮 Pruebas de Reproducción y Verificación Visual

1. **Reproducción de Video ("Play"):**
   - En el dashboard web (`http://localhost:3001`), pulsa el botón verde **Play** en la tarjeta de `screen-01`.
   - Puedes usar la ruta por defecto o desplegar *"Configurar medio / volumen"* para indicar un archivo de video local de tu equipo (ej. `C:\videos\trailer.mp4`) o una URL remota (`http://.../video.mp4`).
   - El agente comenzará la reproducción inmediata en pantalla completa a través de LibVLC.
   - En el panel web:
     - El monitor de TV cambiará automáticamente a **● EN VIVO**.
     - Cada 5 segundos verás el fotograma real actualizado de lo que se está proyectando en la TV/pantalla.
     - Se actualiza el nombre del medio y el badge `PLAYING`.

2. **Pausa y Detención ("Pause" / "Stop"):**
   - Al pulsar **Pausar**, el reproductor alternará la reproducción de LibVLC.
   - Al pulsar **Stop**, LibVLC detiene el video y la miniatura volverá a mostrar el estado de reposo / standby.

3. **Verificación de Pantalla Desconectada ("Sin Señal"):**
   - Presiona `Ctrl + C` en la terminal del agente.
   - Tras aproximadamente 30 segundos de inactividad, el servidor detectará el timeout y el monitor de TV en el panel web pasará automáticamente al estado de **SIN SEÑAL (OFFLINE)** con barras de calibración retro y alerta parpadeante.

---

## 🖥 Control Total (escritorio remoto VNC)

El botón **Control Total** de cada tarjeta abre el escritorio completo de la mini-PC
(ratón, teclado y pantalla) sobre noVNC, en un modal a pantalla completa.

### Cómo viaja la imagen (sin abrir puertos en el stick)

```
Navegador ──WS /ws/vnc/{sessionId}──▶ Pcp.Server ◀──WS saliente /ws/vnc-agent/{sessionId}
                                          │            (lo inicia el agente)
                                          ▼
                                 127.0.0.1:puerto  ← TightVNC en la mini-PC
```

- El VNC **solo escucha en loopback** y con una contraseña de 8 caracteres generada por sesión.
- El agente **sale** hacia el servidor: no hay que abrir el 5900 ni tocar el firewall.
- El servidor únicamente bombea bytes (RFB), sin decodificar ni guardar imagen.

### Requisito: TightVNC en la mini-PC

El agente soporta **TightVNC 2.8.x** (modo aplicación, sin servicio):

1. Si ya está instalado (`tvnserver.exe` en `C:\Program Files\TightVNC` o en
   `Agent:Vnc:InstallDir`), se usa directamente.
2. Si no está, el agente puede aprovisionarlo desde `Agent:Vnc:PackagePath`:
   - `.zip` portable → se extrae en `Agent:Vnc:InstallDir`
     (por defecto `%LOCALAPPDATA%\Pcp\vnc`, sin permisos de administrador).
   - `.exe` / `.msi` → instalación silenciosa (**requiere administrador**).
3. Si no hay ejecutable ni paquete, el panel muestra el motivo exacto y
   `remoteControlAvailable` queda en `false` en el heartbeat.

> El agente debe ejecutarse en la **sesión interactiva** del usuario (no como servicio
> de Windows): TightVNC necesita capturar el escritorio real y enviar entrada.
> Si el servicio de TightVNC está activo, deténlo (`Stop-Service tvnserver`): el agente
> lanza su propia instancia en modo aplicación.

#### Cómo arranca TightVNC (detalles que no se deben "redescubrir")

- El puerto **no** se pasa por línea de comandos: `tvnserver.exe` 2.8 solo acepta `-run`
  (y las opciones de ayuda); con `-port` muestra la ayuda y termina sin arrancar. El agente
  escribe `RfbPort` en el registro y lanza `tvnserver.exe -run`.
- La configuración del modo aplicación vive en `HKCU\Software\TightVNC\Server` y TightVNC
  solo la relee al arrancar, por eso cada sesión reinicia el proceso.
- **`AllowLoopback` debe valer 1.** Con el valor por defecto (0), TightVNC aborta el
  handshake de cualquier conexión desde `127.0.0.1` con
  `Sorry, loopback connections are not enabled`. Ese texto lo emite TightVNC, **no** Kestrel
  ni el túnel WebSocket: no hay nada que habilitar en Kestrel para `ws://` en local.
  `LoopbackOnly=1` cierra el resto de orígenes.
- La contraseña se guarda en `Password` como 8 bytes: **DES/ECB con la clave fija del
  protocolo con los bits invertidos, cifrando la contraseña en claro** (relleno a 8 con ceros).
  El cliente RFB, en cambio, usa como clave la contraseña con los bits invertidos.
- Si existe la marca `HKLM\Software\TightVNC\Server\ServiceOnly`, TightVNC se niega a
  arrancar en modo aplicación; el agente la retira (`Agent:Vnc:ForceApplicationMode`) o
  informa del error si no tiene permisos.
- El agente cierra las instancias previas de `tvnserver` de su propia sesión: una instancia
  huérfana mantiene el mutex global y el proceso nuevo se quedaría bloqueado en
  "TightVNC is already running" sin abrir el puerto.

### Configuración (`Pcp.Agent/appsettings.json`, opcional)

El agente funciona sin `appsettings.json`: si no existe, usa variables de entorno
(`PCP_SERVER_URL`, `PCP_SCREEN_ID`, `PCP_SCREEN_NAME`, `PCP_TOKEN` o `PCP_Agent__*`).
El archivo, si está, se lee desde la misma carpeta que `Pcp.Agent.exe`.

```jsonc
"Agent": {
  "Vnc": {
    "Enabled": true,
    "ForceApplicationMode": true, // retira la marca ServiceOnly de HKLM si puede
    "ExecutablePath": "",         // ruta explícita a tvnserver.exe (opcional)
    "PackagePath": "",            // zip/exe/msi para aprovisionar (opcional)
    "InstallDir": "",             // por defecto %LOCALAPPDATA%\Pcp\vnc
    "StartupTimeoutSeconds": 12,
    "Args": []                    // argumentos extra; por defecto "-run"
  }
}
```

### API y eventos

| Método | Ruta | Comportamiento |
|---|---|---|
| POST | `/api/screens/{id}/remote/start` | Pide al agente abrir VNC + túnel y devuelve `{ sessionId, webSocketUrl, password, viewOnly, expiresInSeconds }`. |
| POST | `/api/screens/{id}/remote/stop` | Cierra la sesión (opcionalmente con `{ sessionId }`). |

- Eventos SignalR nuevos: `OpenVncTunnel` y `CloseVncTunnel` (servidor → agente),
  `VncTunnelStatus` (agente → servidor).
- Una sola sesión por pantalla (409 si ya hay una abierta).
- Si el panel no se conecta en **90 s**, la sesión caduca; el máximo absoluto es **12 h**.
- El cierre del WebSocket (o del agente) desmonta el túnel y detiene TightVNC,
  restaurando la configuración de registro previa.

### Uso

1. Pulsa **Control Total** en la tarjeta de una pantalla online.
2. Espera a "Conectado" (el agente arranca VNC; suele tardar 2–5 s).
3. Haz clic sobre el lienzo para capturar teclado y ratón.
4. Barra superior: **Solo ver / Control**, **Ctrl+Alt+Del**, pantalla completa y cerrar.

> El panel exige login desde la Fase 3. El JWT viaja en una cookie HttpOnly y el
> panel solo guarda una marca de sesión para el middleware de Next.js. Roles:
> `Admin` (control total) y `Viewer` (solo lectura). La API rechaza comandos,
> archivos, wake y control remoto si el rol no es `Admin`.

---

## ⏻ Encendido remoto (Wake-on-LAN)

El servidor vive en la nube y un Magic Packet no cruza Internet, así que el
paquete lo emite **otro agente encendido de la misma red**:

1. El agente detecta su NIC física (excluye Hyper-V, VMware, VPN, WSL, Docker…)
   y reporta MAC/IP/máscara/CIDR al registrarse y en cada heartbeat.
2. Al pulsar **Encender** en una tarjeta offline, el servidor elige un agente
   online con la misma `NetworkCidr` (prefiere el cableado) y le envía el
   comando `wake`.
3. Ese agente difunde el paquete (102 bytes) a `255.255.255.255:9`, al broadcast
   dirigido (`192.168.1.255:9`) y a la última IP conocida, 3 repeticiones.

Requisitos en el stick: WOL activo en BIOS/UEFI, driver de red con *Wake on
Magic Packet* y **Inicio Rápido de Windows desactivado** (con hibernación, S5 no
despierta). Por Wi-Fi no funciona: el panel deshabilita el botón si
`isWired = false`.

Sin ningún agente encendido en esa red no hay quién emita el paquete; la API
responde **503** con un mensaje accionable. Para sedes con una sola pantalla se
puede dejar un stick siempre encendido y fijarlo con `Wol:PreferredRelayScreenId`.

Configuración: `Wol:CooldownSeconds` (20 s por pantalla), `Wol:Port` (9),
`Wol:DirectSendEnabled` (false; solo útil si el servidor está en la LAN) y
`Agent:Wol:InterfaceName` / `Agent:Wol:MacOverride` para forzar la NIC.

---

## ☁ Despliegue en producción

### 1. Base de datos MySQL 8 (Aiven / Clever Cloud)

El servidor usa **Pomelo.EntityFrameworkCore.MySql**. En producción arranca con
`Database.Migrate()` (crea las tablas y aplica migraciones automáticamente).

```powershell
$env:ASPNETCORE_ENVIRONMENT="Production"
$env:DATABASE_URL="mysql://usuario:password@host:3306/pcp?ssl-mode=REQUIRED"
# o bien:
$env:ConnectionStrings__MySql="Server=host;Port=3306;Database=pcp;User ID=usuario;Password=password;SslMode=Required;"
$env:Cors__AllowedOrigins__0="https://panel.example.com"
$env:Auth__SigningKey="clave-aleatoria-de-al-menos-32-caracteres"
$env:Auth__AdminPassword="contraseña-inicial-fuerte"
$env:Auth__CookieSameSite="None"   # panel y API en dominios distintos, siempre con HTTPS
$env:PORT="8080"
cd C:\Proyectos\Pcp\Pcp.Server
dotnet run
```

- La URI `mysql://` se convierte automáticamente a cadena ADO.NET (incluye `ssl-mode`).
- Sin `ASPNETCORE_ENVIRONMENT=Production`, el servidor usa SQLite (`pcp.db`) para desarrollo.
  `EnsureCreated()` no altera tablas existentes: **si cambia el esquema, borra `Pcp.Server/pcp.db`
  (y sus `-shm`/`-wal`) antes de arrancar** para que el modelo se recree.
- Migraciones: `dotnet tool restore` y después
  `dotnet ef migrations add <Nombre> --project Pcp.Server\Pcp.Server.csproj --startup-project Pcp.Server\Pcp.Server.csproj --configuration Release --output-dir Data\Migrations`.
- El arranque reintenta la conexión MySQL 10 veces (5 s entre intentos) para tolerar el arranque en frío de la nube.
- Todas las variables están documentadas en `.env.example`.

### 2. Panel web (Next.js)

`NEXT_PUBLIC_API_URL` se incrusta en **tiempo de build**. Configura el dominio real
en `Pcp.Web/.env.production` (o en el panel de la nube) y compila:

```powershell
cd C:\Proyectos\Pcp\Pcp.Web
npm install
npm run build
npm start        # respeta la variable PORT; output standalone en .next/standalone
```

El panel deduce `wss://` automáticamente cuando se sirve por HTTPS.

### 3. Agente para los sticks (single-file self-contained)

```powershell
powershell -ExecutionPolicy Bypass -File C:\Proyectos\Pcp\scripts\publish-agent.ps1
```

Genera `publish/agent/Pcp.Agent.exe` (~216 MB) + `appsettings.json`. Es autocontenido:
el stick **no** necesita .NET instalado. Para inyectar la IP del servidor sin editar el JSON:

```powershell
$env:PCP_SERVER_URL="https://api.example.com"
$env:PCP_SCREEN_ID="screen-01"
$env:PCP_TOKEN="token-real"
.\Pcp.Agent.exe
```

LibVLC y sus plugins se extraen a un directorio temporal al arrancar; si fallan, el agente
continúa en modo simulado. Pendiente de validación final en un stick real.

El agente es un `WinExe` (no abre consola): toda la actividad se escribe en
`C:\ProgramData\PcpAgent\agent.log` (rotación 5 MB × 3, configurable con `Agent:LogPath`).
En desarrollo, `dotnet run` tampoco imprime en consola: sigue el archivo con `Get-Content -Wait`.

### 4. Instalación zero-touch en decenas de PCs

`publish-agent.ps1` genera `publish/PcpAgent.zip` (~200 MB) con el agente, el instalador y la
plantilla de configuración. El MSI de TightVNC se copia desde `deploy/tightvnc.msi` si existe.

En cada stick:

1. Descomprimir el ZIP (debe contener `Pcp.Agent.exe`, `watchdog.vbs`, `install.bat`, `install.ps1` y, opcionalmente, `tightvnc.msi`).
2. Ejecutar como administrador:

```bat
install.bat PC-SALA-01
install.bat PC-SALA-01 https://pcp-control.onrender.com
install.bat PC-SALA-01 https://pcp-control.onrender.com token-unico
install.bat PC-SALA-01 https://pcp-control.onrender.com token-unico -EnableAutoLogon .\kiosco MiClave123
```

El instalador:

- Instala TightVNC en silencio (`msiexec /qn /norestart`).
- Detiene y deshabilita el servicio TightVNC para que el agente use el modo aplicación en la sesión interactiva, y elimina la marca `ServiceOnly`.
- Copia el agente y `watchdog.vbs` a `C:\Program Files\PcpAgent\` y genera `appsettings.json`.
- Crea la tarea programada `PcpAgent` al iniciar sesión, en la sesión interactiva del usuario actual y con privilegios elevados.
- Crea la tarea `PcpAgentWatchdog`, que reinicia el agente en 10 s si se cierra o falla (log en `C:\ProgramData\PcpAgent\watchdog.log`).
- Con `-EnableAutoLogon` configura el auto-logon de Windows para que el kiosco arranque al encender la PC (la contraseña queda en el registro; usa un usuario kiosco dedicado).
- Concede permisos de escritura al grupo Usuarios en `C:\ProgramData\PcpAgent` para los logs del agente.
- Arranca el agente y deja el log de instalación en `C:\ProgramData\PcpAgent\install.log`.

La configuración efectiva prioriza el entorno sobre el JSON:

```
appsettings.json < variables estándar < PCP_* < PCP_SERVER_URL/PCP_SCREEN_ID/PCP_TOKEN
```

El arranque muestra `[CONFIG] ScreenId=... ServerUrl=... Origen=...` para diagnosticar instalaciones
masivas. Para reconfigurar por RMM sin tocar archivos, ejecutar `install.ps1 ... -UseMachineEnvironment`.

Desinstalación: `powershell -ExecutionPolicy Bypass -File uninstall.ps1` (detiene y elimina ambas
tareas, mata el watchdog y borra la carpeta; no desinstala TightVNC). Añade `-ClearAutoLogon` si el
instalador había habilitado el auto-logon.
