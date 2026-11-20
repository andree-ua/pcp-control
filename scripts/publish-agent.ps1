param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = ".\publish\agent"
)

$ErrorActionPreference = "Stop"

Write-Host "Publicando Pcp.Agent como ejecutable unico self-contained..." -ForegroundColor Cyan

dotnet publish "$PSScriptRoot\..\Pcp.Agent\Pcp.Agent.csproj" `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $Output

if ($LASTEXITCODE -ne 0) {
    throw "La publicacion del agente fallo (exit code $LASTEXITCODE)."
}

Write-Host ""
Write-Host "Listo. Contenido de $Output :" -ForegroundColor Green
Get-ChildItem $Output | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Copiar la carpeta completa al stick (el .exe y appsettings.json)." -ForegroundColor Yellow
