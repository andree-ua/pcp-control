using System.Collections.Concurrent;

namespace Pcp.Server.Services;

/// <summary>
/// Interfaz para el gestor de conexiones activas de agentes de pantallas.
/// </summary>
public interface IScreenConnectionManager
{
    void RegisterConnection(string screenId, string connectionId);
    void RemoveConnection(string connectionId);
    string? GetConnectionId(string screenId);
    string? GetScreenId(string connectionId);
}

/// <summary>
/// Mantiene en memoria el mapeo bidireccional seguro entre ScreenId y ConnectionId de SignalR.
/// </summary>
public class ScreenConnectionManager : IScreenConnectionManager
{
    private readonly ConcurrentDictionary<string, string> _screenToConn = new();
    private readonly ConcurrentDictionary<string, string> _connToScreen = new();

    public void RegisterConnection(string screenId, string connectionId)
    {
        _screenToConn[screenId] = connectionId;
        _connToScreen[connectionId] = screenId;
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connToScreen.TryRemove(connectionId, out var screenId))
        {
            // Solo borrar el mapeo directo si sigue apuntando a esta conexión.
            // Una conexión duplicada que se cae no debe dejar sin mapeo a la vigente.
            if (_screenToConn.TryGetValue(screenId, out var currentConnection) &&
                currentConnection == connectionId)
            {
                _screenToConn.TryRemove(screenId, out _);
            }
        }
    }

    public string? GetConnectionId(string screenId)
    {
        _screenToConn.TryGetValue(screenId, out var connId);
        return connId;
    }

    public string? GetScreenId(string connectionId)
    {
        _connToScreen.TryGetValue(connectionId, out var screenId);
        return screenId;
    }
}
