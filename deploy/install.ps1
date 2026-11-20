#Requires -Version 5.1
<#
.SYNOPSIS
    Instalación zero-touch del Agente PCP en una mini-PC o stick Windows.

.DESCRIPTION
    - Instala TightVNC en modo silencioso (tightvnc.msi junto a este script).
    - Desactiva el servicio TightVNC para que el agente use el modo aplicación
      en la sesión interactiva (el servicio vive en sesión 0 y bloquea el kiosco).
    - Copia Pcp.Agent.exe y genera appsettings.json en C:\Program Files\PcpAgent.
    - Crea una tarea programada al iniciar sesión del usuario interactivo.

.EJEMPLOS
    .\install.ps1 -ScreenId PC-SALA-01
    .\install.ps1 -ScreenId PC-SALA-01 -ServerUrl https://pcp-control.onrender.com
    .\install.ps1 -ScreenId PC-SALA-01 -Token token-unico -UseMachineEnvironment
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ScreenId,

    [string]$ServerUrl = "https://pcp-control.onrender.com",
    [string]$Token = "dev-token",
    [string]$InstallDir = "",
    [string]$TaskName = "PcpAgent",
    [string]$WatchdogTaskName = "PcpAgentWatchdog",
    [string]$RunAsUser = "",
    [string]$VncMsi = "",
    [switch]$UseMachineEnvironment,
    [switch]$EnableAutoLogon,
    [string]$AutoLogonUser = "",
    [string]$AutoLogonPassword = ""
)

$ErrorActionPreference = "Stop"

# Los valores vacíos pasados por install.bat (argumentos opcionales) usan el default.
if ([string]::IsNullOrWhiteSpace($ServerUrl)) { $ServerUrl = "https://pcp-control.onrender.com" }
if ([string]::IsNullOrWhiteSpace($Token)) { $Token = "dev-token" }
if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $env:ProgramFiles "PcpAgent" }
if ([string]::IsNullOrWhiteSpace($RunAsUser)) {
    $RunAsUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

$ServerUrl = $ServerUrl.Trim().TrimEnd('/')
if ($ServerUrl -notmatch '^https?://') {
    $ServerUrl = "https://$ServerUrl"
}

$ScreenId = $ScreenId.Trim()

# ── Elevación a administrador ────────────────────────────────────────────────
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Se necesitan permisos de administrador. Solicitando elevacion..." -ForegroundColor Yellow

    function Quote-ProcessArgument([string]$Value) {
        return '"' + ($Value -replace '"', '\"') + '"'
    }

    $elevatedArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (Quote-ProcessArgument $PSCommandPath),
        "-ScreenId", (Quote-ProcessArgument $ScreenId),
        "-ServerUrl", (Quote-ProcessArgument $ServerUrl),
        "-Token", (Quote-ProcessArgument $Token),
        "-InstallDir", (Quote-ProcessArgument $InstallDir),
        "-TaskName", (Quote-ProcessArgument $TaskName),
        "-WatchdogTaskName", (Quote-ProcessArgument $WatchdogTaskName),
        "-RunAsUser", (Quote-ProcessArgument $RunAsUser)
    )

    if ($UseMachineEnvironment) { $elevatedArgs += "-UseMachineEnvironment" }
    if ($EnableAutoLogon) {
        $elevatedArgs += @(
            "-EnableAutoLogon",
            "-AutoLogonUser", (Quote-ProcessArgument $AutoLogonUser),
            "-AutoLogonPassword", (Quote-ProcessArgument $AutoLogonPassword)
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($VncMsi)) {
        $elevatedArgs += @("-VncMsi", (Quote-ProcessArgument $VncMsi))
    }

    try {
        $process = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $elevatedArgs -Wait -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Host "No se pudo iniciar la instalacion elevada: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# ── Utilidades de log ────────────────────────────────────────────────────────
$logDir = Join-Path $env:ProgramData "PcpAgent"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "install.log"

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO"
    )

    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    $color = switch ($Level) {
        "WARN" { "Yellow" }
        "ERROR" { "Red" }
        default { "Gray" }
    }

    Write-Host $line -ForegroundColor $color
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

function Find-TightVnc {
    $candidates = @()
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "TightVNC\tvnserver.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "TightVNC\tvnserver.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Disable-TightVncService {
    $services = Get-Service -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq "tvnserver" -or $_.DisplayName -like "*TightVNC*"
    }

    foreach ($service in $services) {
        try {
            if ($service.Status -ne "Stopped") {
                Stop-Service -Name $service.Name -Force -ErrorAction Stop
                Write-Log "Servicio TightVNC detenido: $($service.Name)"
            }

            Set-Service -Name $service.Name -StartupType Disabled -ErrorAction Stop

            # Por si una acción de recuperación lo reinició entre Stop y Set.
            $current = Get-Service -Name $service.Name -ErrorAction SilentlyContinue
            if ($null -ne $current -and $current.Status -ne "Stopped") {
                Stop-Service -Name $service.Name -Force -ErrorAction SilentlyContinue
            }

            Write-Log "Servicio TightVNC deshabilitado: $($service.Name)"
        }
        catch {
            Write-Log "No se pudo detener/deshabilitar el servicio $($service.Name): $($_.Exception.Message)" "WARN"
        }
    }
}

function Remove-ServiceOnlyFlag {
    $views = @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)

    foreach ($view in $views) {
        $base = $null
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $key = $base.OpenSubKey("Software\TightVNC\Server", $true)
            if ($null -ne $key -and ($key.GetSubKeyNames() -contains "ServiceOnly")) {
                $key.DeleteSubKeyTree("ServiceOnly", $false)
                Write-Log "Eliminada la marca ServiceOnly de TightVNC ($view)."
            }
        }
        catch {
            Write-Log "No se pudo revisar ServiceOnly ($view): $($_.Exception.Message)" "WARN"
        }
        finally {
            if ($null -ne $key) { $key.Close() }
            if ($null -ne $base) { $base.Close() }
        }
    }
}

function Grant-LogPermissions {
    # El agente se ejecuta como el usuario interactivo (sin elevar), así que necesita
    # poder escribir agent.log y su rotación dentro de ProgramData.
    try {
        $usersSid = "S-1-5-32-545" # BUILTIN\Users, independiente del idioma de Windows
        $output = & icacls.exe $logDir /grant "*${usersSid}:(OI)(CI)M" /T /C 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Log "icacls devolvió $LASTEXITCODE al ajustar permisos de ${logDir}: $output" "WARN"
        }
        else {
            Write-Log "Permisos de escritura concedidos al grupo Usuarios en $logDir."
        }
    }
    catch {
        Write-Log "No se pudieron ajustar los permisos de ${logDir}: $($_.Exception.Message)" "WARN"
    }
}

function Stop-AgentTasks {
    param(
        [string]$PrimaryTask,
        [string]$WatchdogTask
    )

    # El flag detiene al watchdog antes de tocar el agente (actualizaciones limpias).
    $stopFlag = Join-Path $logDir "watchdog.stop"
    try {
        Set-Content -LiteralPath $stopFlag -Value (Get-Date -Format "o") -Encoding ASCII
    }
    catch {
        Write-Log "No se pudo crear el flag de parada del watchdog: $($_.Exception.Message)" "WARN"
    }

    foreach ($name in @($WatchdogTask, $PrimaryTask)) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Write-Log "Deteniendo la tarea programada existente '$name'."
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    # Por si el proceso residente del watchdog quedó vivo tras desregistrar su tarea.
    try {
        Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -like "*watchdog.vbs*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    catch {
        Write-Log "No se pudo detener el proceso del watchdog: $($_.Exception.Message)" "WARN"
    }

    Get-Process -Name "Pcp.Agent" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Enable-WindowsAutoLogon {
    param(
        [string]$UserName,
        [string]$Password
    )

    if ([string]::IsNullOrWhiteSpace($UserName)) {
        Write-Log "Auto-logon solicitado sin usuario: se omite." "WARN"
        return
    }

    if ([string]::IsNullOrWhiteSpace($Password)) {
        Write-Log "Auto-logon solicitado sin contraseña: Windows no puede iniciar sesión solo. Se omite." "WARN"
        return
    }

    $domain = $env:COMPUTERNAME
    $account = $UserName.Trim()
    if ($account.Contains("\")) {
        $parts = $account.Split("\", 2)
        $domain = $parts[0]
        $account = $parts[1]
    }

    $winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty -Path $winlogon -Name "AutoAdminLogon" -Value "1" -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultUserName" -Value $account -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultDomainName" -Value $domain -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultPassword" -Value $Password -Type String

    Write-Log "Auto-logon configurado para '$domain\$account'."
    Write-Log "AVISO DE SEGURIDAD: la contraseña queda en texto claro en el registro de Windows. Usa un usuario kiosco dedicado y sin privilegios de administrador." "WARN"
}

# ── Instalación ──────────────────────────────────────────────────────────────
try {
    Write-Log "=============================================================="
    Write-Log "Instalando Agente PCP para la pantalla '$ScreenId'"
    Write-Log "Servidor: $ServerUrl"
    Write-Log "Usuario de la tarea: $RunAsUser"
    Write-Log "Carpeta de instalacion: $InstallDir"

    if ($ScreenId -notmatch '^[A-Za-z0-9._-]{1,50}$') {
        throw "El ID de pantalla '$ScreenId' no es válido. Usa letras, números, punto, guion o guion bajo (máx. 50)."
    }

    $serverUri = $null
    if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$serverUri) -or
        ($serverUri.Scheme -ne "http" -and $serverUri.Scheme -ne "https")) {
        throw "La URL del servidor '$ServerUrl' no es válida. Debe ser http:// o https://."
    }

    $agentSource = Join-Path $PSScriptRoot "Pcp.Agent.exe"
    if (-not (Test-Path -LiteralPath $agentSource)) {
        throw "No se encontró Pcp.Agent.exe junto a install.ps1 ($PSScriptRoot). Copia el ejecutable en la misma carpeta."
    }

    $templateSource = Join-Path $PSScriptRoot "appsettings.template.json"
    if (-not (Test-Path -LiteralPath $templateSource)) {
        throw "No se encontró appsettings.template.json junto a install.ps1."
    }

    # 1. TightVNC: si no está instalado, instalar el MSI en silencio.
    $tvnPath = Find-TightVnc
    $msiPath = if ([string]::IsNullOrWhiteSpace($VncMsi)) {
        Join-Path $PSScriptRoot "tightvnc.msi"
    }
    else {
        [System.IO.Path]::GetFullPath($VncMsi)
    }

    if (-not $tvnPath) {
        if (-not (Test-Path -LiteralPath $msiPath)) {
            throw "No se encontró TightVNC ni el instalador '$msiPath'. Copia tightvnc.msi junto a install.bat."
        }

        Write-Log "Instalando TightVNC en modo silencioso desde $msiPath"
        $msiLog = Join-Path $logDir "tightvnc-msi.log"
        $msiArgs = @(
            "/i", ('"{0}"' -f $msiPath),
            "/qn", "/norestart",
            "/l*v", ('"{0}"' -f $msiLog)
        )

        $msiProcess = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru -NoNewWindow
        if ($msiProcess.ExitCode -notin @(0, 3010)) {
            throw "El instalador de TightVNC terminó con código $($msiProcess.ExitCode). Revisa $msiLog."
        }
        if ($msiProcess.ExitCode -eq 3010) {
            Write-Log "TightVNC instalado; Windows solicita reinicio (código 3010)." "WARN"
        }

        $tvnPath = Find-TightVnc
        if (-not $tvnPath) {
            throw "TightVNC se instaló pero no se encontró tvnserver.exe en Program Files."
        }
    }
    else {
        Write-Log "TightVNC ya está instalado en $tvnPath; se omite el MSI."
    }

    # 2. El MSI instala TightVNC como servicio (sesión 0). El agente necesita
    #    modo aplicación en la sesión interactiva: se detiene y deshabilita.
    Disable-TightVncService
    Remove-ServiceOnlyFlag

    # 3. Detener una instalación anterior del agente y su watchdog.
    Stop-AgentTasks -PrimaryTask $TaskName -WatchdogTask $WatchdogTaskName

    # 4. Copiar el agente y el watchdog.
    Write-Log "Copiando el agente en $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $agentTarget = Join-Path $InstallDir "Pcp.Agent.exe"
    Copy-Item -LiteralPath $agentSource -Destination $agentTarget -Force
    try { Unblock-File -LiteralPath $agentTarget -ErrorAction SilentlyContinue } catch { }

    $watchdogSource = Join-Path $PSScriptRoot "watchdog.vbs"
    if (-not (Test-Path -LiteralPath $watchdogSource)) {
        throw "No se encontró watchdog.vbs junto a install.ps1."
    }
    $watchdogTarget = Join-Path $InstallDir "watchdog.vbs"
    Copy-Item -LiteralPath $watchdogSource -Destination $watchdogTarget -Force
    Write-Log "Watchdog copiado en $watchdogTarget"

    # Permisos de escritura de logs para el usuario interactivo.
    Grant-LogPermissions

    # 5. Generar appsettings.json desde la plantilla.
    $config = Get-Content -LiteralPath $templateSource -Raw | ConvertFrom-Json
    $config.Agent.ScreenId = $ScreenId
    $config.Agent.Name = "Pantalla $ScreenId"
    $config.Agent.Token = $Token
    $config.Agent.ServerUrl = $ServerUrl
    $config.Agent.DevMode = $false
    $config.Agent.AllowSystemCommands = $true
    $config.Agent.Vnc.Enabled = $true
    $config.Agent.Vnc.ForceApplicationMode = $true
    if ($tvnPath) { $config.Agent.Vnc.ExecutablePath = $tvnPath }

    $settingsPath = Join-Path $InstallDir "appsettings.json"
    $settingsJson = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($settingsPath, $settingsJson, (New-Object System.Text.UTF8Encoding($false)))
    Write-Log "Configuracion generada: $settingsPath"

    # 6. Variables de entorno de máquina (opcional, para gestión remota).
    if ($UseMachineEnvironment) {
        [Environment]::SetEnvironmentVariable("PCP_SERVER_URL", $ServerUrl, "Machine")
        [Environment]::SetEnvironmentVariable("PCP_SCREEN_ID", $ScreenId, "Machine")
        [Environment]::SetEnvironmentVariable("PCP_SCREEN_NAME", "Pantalla $ScreenId", "Machine")
        [Environment]::SetEnvironmentVariable("PCP_TOKEN", $Token, "Machine")
        Write-Log "Variables PCP_* definidas a nivel de máquina (tienen prioridad sobre el JSON)."
    }

    # 7. Auto-logon opcional: el kiosco debe arrancar al encender la PC y las tareas
    #    interactivas necesitan una sesión de usuario iniciada (la sesión 0 no tiene
    #    escritorio y rompería LibVLC, TightVNC y el bloqueo de sesión).
    if ($EnableAutoLogon) {
        Enable-WindowsAutoLogon -UserName $AutoLogonUser -Password $AutoLogonPassword
    }
    else {
        Write-Log "Auto-logon no configurado: el kiosco arrancará cuando un usuario inicie sesión. Usa -EnableAutoLogon (o netplwiz) para arranque zero-touch." "WARN"
    }

    # 8. Tarea programada al iniciar sesión, en la sesión interactiva del usuario.
    Write-Log "Registrando la tarea programada '$TaskName' para $RunAsUser"
    $action = New-ScheduledTaskAction -Execute $agentTarget -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
        -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $taskPrincipal `
        -Settings $settings `
        -Description "Agente PCP - control remoto y reproduccion kiosco" `
        -Force | Out-Null

    # 9. Watchdog residente (wscript, sin ventana): reinicia el agente en <= 10 s.
    Write-Log "Registrando la tarea watchdog '$WatchdogTaskName'"
    $wscriptExe = Join-Path $env:SystemRoot "System32\wscript.exe"
    $watchdogAction = New-ScheduledTaskAction -Execute $wscriptExe `
        -Argument ('//B "{0}"' -f $watchdogTarget) `
        -WorkingDirectory $InstallDir
    $watchdogTrigger = New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
    $watchdogSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
        -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew `
        -Hidden

    Register-ScheduledTask `
        -TaskName $WatchdogTaskName `
        -Action $watchdogAction `
        -Trigger $watchdogTrigger `
        -Principal $taskPrincipal `
        -Settings $watchdogSettings `
        -Description "Watchdog del Agente PCP - reinicia el agente si se cierra" `
        -Force | Out-Null

    # 10. Arrancar ahora para validar la instalación. Si falla el arranque
    #     inmediato, las tareas seguirán disponibles para el próximo inicio de sesión.
    try {
        $stopFlag = Join-Path $logDir "watchdog.stop"
        Remove-Item -LiteralPath $stopFlag -Force -ErrorAction SilentlyContinue

        Write-Log "Iniciando la tarea programada..."
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 3

        $agentProcess = Get-Process -Name "Pcp.Agent" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $agentProcess) {
            Write-Log "Agente en ejecución (PID $($agentProcess.Id))."
        }
        else {
            Write-Log "La tarea se registró pero el agente aún no aparece en ejecución. Revisa el Programador de tareas." "WARN"
        }

        Write-Log "Iniciando el watchdog..."
        Start-ScheduledTask -TaskName $WatchdogTaskName
    }
    catch {
        Write-Log "Las tareas se registraron, pero no se pudieron iniciar en este momento: $($_.Exception.Message)" "WARN"
    }

    Write-Log "Instalación completada correctamente."
    Write-Log "=============================================================="
    exit 0
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)" "ERROR"
    Write-Log "Revisa el log completo en $logFile" "ERROR"
    Write-Log "=============================================================="
    exit 1
}
