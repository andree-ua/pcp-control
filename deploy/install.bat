@echo off
setlocal

if "%~1"=="" (
    echo.
    echo Uso: install.bat ^<ID-PANTALLA^> [URL_SERVIDOR] [TOKEN] [-EnableAutoLogon USUARIO CONTRASENA]
    echo Ejemplo: install.bat PC-SALA-01 https://pcp-control.onrender.com dev-token
    echo Auto-logon: install.bat PC-SALA-01 https://pcp-control.onrender.com dev-token -EnableAutoLogon .\kiosco MiClave123
    echo.
    echo Nota: si la contrasena tiene caracteres especiales, ejecuta install.ps1 directamente.
    exit /b 1
)

rem Auto-logon opcional para que el kiosco arranque al encender la PC sin intervencion.
set "AUTOLOGON_ARGS="
if /I "%~4"=="-EnableAutoLogon" (
    if "%~5"=="" (
        echo Falta el usuario para el auto-logon.
        exit /b 1
    )
    set "AUTOLOGON_ARGS=-EnableAutoLogon -AutoLogonUser "%~5" -AutoLogonPassword "%~6""
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -ScreenId "%~1" -ServerUrl "%~2" -Token "%~3" %AUTOLOGON_ARGS%
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo La instalacion fallo con codigo %EXITCODE%. Revisa C:\ProgramData\PcpAgent\install.log
    pause
)

exit /b %EXITCODE%
