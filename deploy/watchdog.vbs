Option Explicit

' ---------------------------------------------------------------------------
' Watchdog del Agente PCP.
'
' Windows Task Scheduler no permite intervalos de reinicio menores a 1 minuto,
' asi que este script residente (wscript, sin ventana) comprueba cada 10 s que
' Pcp.Agent.exe siga vivo y lo relanza si se ha cerrado o ha fallado.
'
' - Se ejecuta en la sesion interactiva del usuario (LogonType Interactive),
'   igual que el agente: el kiosco y VNC necesitan escritorio real.
' - El flag C:\ProgramData\PcpAgent\watchdog.stop lo detiene durante una
'   actualizacion o desinstalacion.
' - Los reinicios se registran en C:\ProgramData\PcpAgent\watchdog.log.
' ---------------------------------------------------------------------------

Const AGENT_EXE = "Pcp.Agent.exe"
Const DATA_DIR = "C:\ProgramData\PcpAgent"
Const LOG_PATH = "C:\ProgramData\PcpAgent\watchdog.log"
Const STOP_FLAG = "C:\ProgramData\PcpAgent\watchdog.stop"
Const CHECK_SECONDS = 10

Dim fso, shell, wmi, installDir, agentPath

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
installDir = fso.GetParentFolderName(WScript.ScriptFullName)
agentPath = installDir & "\" & AGENT_EXE

If Not fso.FolderExists(DATA_DIR) Then
    On Error Resume Next
    fso.CreateFolder(DATA_DIR)
    On Error GoTo 0
End If

Set wmi = GetObject("winmgmts:\\.\root\cimv2")

LogLine "Watchdog iniciado. Agente: " & agentPath

Do
    If fso.FileExists(STOP_FLAG) Then
        LogLine "Flag de parada detectado; el watchdog termina."
        Exit Do
    End If

    If Not IsAgentRunning() Then
        If fso.FileExists(agentPath) Then
            On Error Resume Next
            shell.CurrentDirectory = installDir
            shell.Run """" & agentPath & """", 0, False
            If Err.Number <> 0 Then
                LogLine "ERROR al iniciar el agente: " & Err.Description
                Err.Clear
            Else
                LogLine "Agente no encontrado. Reiniciado automaticamente."
            End If
            On Error GoTo 0
            ' Margen para que el single-file se extraiga y arranque antes de
            ' volver a comprobar (evita lanzamientos en cadena).
            WScript.Sleep 15000
        Else
            LogLine "No existe " & agentPath & "; no se puede reiniciar."
            WScript.Sleep 60000
        End If
    End If

    WScript.Sleep CHECK_SECONDS * 1000
Loop

Sub LogLine(message)
    On Error Resume Next
    Dim ts
    Set ts = fso.OpenTextFile(LOG_PATH, 8, True)
    If Err.Number = 0 Then
        ts.WriteLine "[" & FormatDateTime(Now, 2) & " " & FormatDateTime(Now, 4) & "] " & message
        ts.Close
    End If
    Err.Clear
    On Error GoTo 0
End Sub

Function IsAgentRunning()
    Dim processes
    IsAgentRunning = False

    On Error Resume Next
    Set processes = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='" & AGENT_EXE & "'")
    If Err.Number = 0 Then
        If processes.Count > 0 Then
            IsAgentRunning = True
        End If
    End If
    Err.Clear
    On Error GoTo 0
End Function
