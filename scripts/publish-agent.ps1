param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = ".\publish\agent"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $repoRoot $Output
}
$Output = [System.IO.Path]::GetFullPath($Output)

# Salida intermedia aislada: evita conflictos con un agente en ejecución que
# bloquee bin\Release y mantiene limpio el árbol del proyecto.
$intermediateOutput = Join-Path $repoRoot ".build\agent\bin"

Write-Host "Publicando Pcp.Agent como ejecutable unico self-contained..." -ForegroundColor Cyan

dotnet publish (Join-Path $repoRoot "Pcp.Agent\Pcp.Agent.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:OutputPath="$intermediateOutput" `
    --output $Output

if ($LASTEXITCODE -ne 0) {
    throw "La publicacion del agente fallo (exit code $LASTEXITCODE)."
}

# Archivos del instalador zero-touch.
$deployDir = Join-Path $repoRoot "deploy"
$installerFiles = @(
    "install.bat",
    "install.ps1",
    "uninstall.ps1",
    "watchdog.vbs",
    "appsettings.template.json"
)

foreach ($file in $installerFiles) {
    $source = Join-Path $deployDir $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Falta el archivo del instalador: $source"
    }
    Copy-Item -LiteralPath $source -Destination $Output -Force
}

# El MSI de TightVNC es opcional en el repositorio: si esta, se empaqueta.
$tightVncMsi = Join-Path $deployDir "tightvnc.msi"
if (Test-Path -LiteralPath $tightVncMsi) {
    Copy-Item -LiteralPath $tightVncMsi -Destination $Output -Force
    Write-Host "Incluido tightvnc.msi en el paquete." -ForegroundColor Green
}
else {
    Write-Host "Aviso: no hay deploy\tightvnc.msi; el tecnico debera copiarlo junto a install.bat." -ForegroundColor Yellow
}

# Limpiar archivos que no forman parte del paquete de despliegue.
Remove-Item (Join-Path $Output "appsettings.Development.json") -Force -ErrorAction SilentlyContinue
Get-ChildItem $Output -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

# ZIP listo para copiar a los sticks.
$zipPath = Join-Path (Split-Path $Output -Parent) "PcpAgent.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Generando $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Listo. Contenido de $Output :" -ForegroundColor Green
Get-ChildItem $Output | Select-Object Name, Length | Format-Table -AutoSize

$zipInfo = Get-Item -LiteralPath $zipPath
Write-Host ("ZIP generado: {0} ({1:N1} MB)" -f $zipInfo.FullName, ($zipInfo.Length / 1MB)) -ForegroundColor Green
Write-Host "Despliegue: copiar el ZIP al stick, descomprimir y ejecutar install.bat <ID-PANTALLA>." -ForegroundColor Yellow
