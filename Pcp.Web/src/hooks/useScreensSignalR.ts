"use client";

import { useEffect, useState, useCallback, useRef } from "react";
import * as signalR from "@microsoft/signalr";
import { CommandResultDto, ScreenInfo, ThumbnailDto, ToastMessage } from "@/types/screen";
import { fetchScreens, SERVER_URL } from "@/services/api";

export function useScreensSignalR() {
  const [screens, setScreens] = useState<ScreenInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<"connected" | "reconnecting" | "disconnected">("disconnected");
  const [toasts, setToasts] = useState<ToastMessage[]>([]);

  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const addToast = useCallback((title: string, message: string, type: "success" | "error" | "info" = "info") => {
    const id = `${Date.now()}-${Math.random()}`;
    setToasts((prev) => [...prev, { id, title, message, type }]);

    // Auto-eliminar a los 4 segundos
    setTimeout(() => {
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, 4000);
  }, []);

  const removeToast = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const loadScreens = useCallback(async () => {
    try {
      setError(null);
      const data = await fetchScreens();
      setScreens(data);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Error desconocido al cargar pantallas";
      setError(msg);
      addToast("Error de conexión", msg, "error");
    } finally {
      setLoading(false);
    }
  }, [addToast]);

  useEffect(() => {
    // 1. Cargar datos iniciales vía REST
    loadScreens();

    // 2. Crear conexión SignalR al Hub con transporte WebSockets y credenciales
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${SERVER_URL}/hubs/screen`, {
        transport:
          signalR.HttpTransportType.WebSockets |
          signalR.HttpTransportType.LongPolling,
        withCredentials: true,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount === 0) return 0;
          if (retryContext.previousRetryCount < 3) return 2000;
          return 5000;
        },
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    connectionRef.current = connection;

    // Escuchar Heartbeats en tiempo real
    connection.on("HeartbeatReceived", (updatedScreen: ScreenInfo) => {
      setScreens((prev) => {
        const index = prev.findIndex((s) => s.id === updatedScreen.id);
        if (index >= 0) {
          const clone = [...prev];
          clone[index] = { ...clone[index], ...updatedScreen, status: "online" };
          return clone;
        } else {
          return [...prev, updatedScreen];
        }
      });
    });

    // Escuchar cambios de estado (online/offline)
    connection.on("ScreenStatusChanged", (screenId: string, status: string) => {
      setScreens((prev) =>
        prev.map((s) => (s.id === screenId ? { ...s, status } : s))
      );
    });

    // Escuchar miniaturas en tiempo real (Thumbnails Base64)
    connection.on("ThumbnailReceived", (dto: ThumbnailDto) => {
      setScreens((prev) =>
        prev.map((s) =>
          s.id === dto.screenId ? { ...s, thumbnailBase64: dto.thumbnailBase64 } : s
        )
      );
    });

    // Escuchar resultados de comandos
    connection.on("CommandResultReceived", (result: CommandResultDto) => {
      const isSuccess = result.status.toLowerCase() === "ok";
      addToast(
        isSuccess ? "Comando Ejecutado" : "Error en Comando",
        `Pantalla [${result.screenId}]: ${result.detail || result.status}`,
        isSuccess ? "success" : "error"
      );
    });

    // Eventos de estado de conexión
    connection.onreconnecting(() => {
      setConnectionStatus("reconnecting");
    });

    connection.onreconnected(() => {
      setConnectionStatus("connected");
      loadScreens(); // Sincronizar tras reconexión
    });

    connection.onclose(() => {
      setConnectionStatus("disconnected");
    });

    // Iniciar conexión
    connection
      .start()
      .then(() => {
        setConnectionStatus("connected");
      })
      .catch((err) => {
        setConnectionStatus("disconnected");
        console.warn("No se pudo conectar a SignalR Hub de inmediato:", err);
      });

    return () => {
      if (connection) {
        connection.stop();
      }
    };
  }, [loadScreens, addToast]);

  return {
    screens,
    loading,
    error,
    connectionStatus,
    toasts,
    addToast,
    removeToast,
    refreshScreens: loadScreens,
  };
}
