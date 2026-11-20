#Requires -Version 5.1
<#
.SYNOPSIS
    Desinstala el Agente PCP de una máquina (no desinstala TightVNC).
#>
[CmdletBinding()]
param(
    [string]$TaskName = "PcpAgent",
    [string]$WatchdogTaskName = "PcpAgentWatchdog",
    [string]$InstallDir = "",
    [switch]$ClearMachineEnvironment,
    [switch]$ClearAutoLogon
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:ProgramFiles "PcpAgent"
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $PSCommandPath),
        "-TaskName", ('"{0}"' -f $TaskName),
        "-WatchdogTaskName", ('"{0}"' -f $WatchdogTaskName),
        "-InstallDir", ('"{0}"' -f $InstallDir)
    )
    if ($ClearMachineEnvironment) { $arguments += "-ClearMachineEnvironment" }
    if ($ClearAutoLogon) { $arguments += "-ClearAutoLogon" }

    $process = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}

$logDir = Join-Path $env:ProgramData "PcpAgent"
$stopFlag = Join-Path $logDir "watchdog.stop"

# 1. Detener el watchdog antes que nada: si sigue vivo, relanzaría el agente.
try {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Set-Content -LiteralPath $stopFlag -Value (Get-Date -Format "o") -Encoding ASCII
}
catch {
    Write-Host "Aviso: no se pudo crear el flag de parada del watchdog: $($_.Exception.Message)" -ForegroundColor Yellow
}

foreach ($name in @($WatchdogTaskName, $TaskName)) {
    $existingTask = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($null -ne $existingTask) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Write-Host "Tarea programada '$name' eliminada."
    }
}

try {
    Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*watchdog.vbs*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
catch {
    Write-Host "Aviso: no se pudo detener el proceso del watchdog: $($_.Exception.Message)" -ForegroundColor Yellow
}

Get-Process -Name "Pcp.Agent" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Carpeta '$InstallDir' eliminada."
}

# 2. Limpiar el flag (la carpeta de logs se conserva para diagnóstico).
Remove-Item -LiteralPath $stopFlag -Force -ErrorAction SilentlyContinue

if ($ClearMachineEnvironment) {
    foreach ($name in @("PCP_SERVER_URL", "PCP_SCREEN_ID", "PCP_SCREEN_NAME", "PCP_TOKEN")) {
        [Environment]::SetEnvironmentVariable($name, $null, "Machine")
    }
    Write-Host "Variables PCP_* de máquina eliminadas."
}

if ($ClearAutoLogon) {
    $winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    try {
        Set-ItemProperty -Path $winlogon -Name "AutoAdminLogon" -Value "0" -Type String
        Remove-ItemProperty -Path $winlogon -Name "DefaultPassword" -ErrorAction SilentlyContinue
        Write-Host "Auto-logon de Windows deshabilitado."
    }
    catch {
        Write-Host "Aviso: no se pudo deshabilitar el auto-logon: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host "Agente PCP desinstalado. TightVNC permanece instalado."
Write-Host "Los logs se conservan en $logDir."
exit 0
