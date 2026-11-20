"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type RFBType from "@novnc/novnc/lib/rfb";
import {
  AlertTriangle,
  Eye,
  Keyboard,
  Loader2,
  Maximize2,
  Minimize2,
  MonitorSmartphone,
  RefreshCw,
  X,
} from "lucide-react";
import { resolveVncSocketUrl, startRemoteSession, stopRemoteSession } from "@/services/api";
import type { RemoteSession } from "@/types/screen";

interface RemoteDesktopModalProps {
  open: boolean;
  screenId: string;
  screenName?: string;
  onClose: () => void;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

type ConnectionState = "starting" | "connecting" | "connected" | "error" | "closed";

const STATE_LABEL: Record<ConnectionState, string> = {
  starting: "Preparando el agente...",
  connecting: "Conectando al escritorio...",
  connected: "Conectado",
  error: "Sin conexión",
  closed: "Sesión cerrada",
};

export function RemoteDesktopModal({
  open,
  screenId,
  screenName,
  onClose,
  onSuccess,
  onError,
}: RemoteDesktopModalProps) {
  const [state, setState] = useState<ConnectionState>("starting");
  const [message, setMessage] = useState<string>("Solicitando sesión de control remoto...");
  const [viewOnly, setViewOnly] = useState(false);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [attempt, setAttempt] = useState(0);

  const containerRef = useRef<HTMLDivElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const rfbRef = useRef<RFBType | null>(null);

  const onCloseRef = useRef(onClose);
  const onSuccessRef = useRef(onSuccess);
  const onErrorRef = useRef(onError);

  useEffect(() => {
    onCloseRef.current = onClose;
    onSuccessRef.current = onSuccess;
    onErrorRef.current = onError;
  }, [onClose, onSuccess, onError]);

  // Bloquea el scroll del panel mientras el escritorio remoto está abierto
  useEffect(() => {
    if (!open) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [open]);

  // Ciclo de vida de la sesión: pedir sesión -> conectar noVNC -> limpiar
  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    let session: RemoteSession | null = null;
    let rfb: RFBType | null = null;

    const run = async () => {
      setState("starting");
      setMessage("Solicitando sesión de control remoto...");
      setIsFullscreen(false);
      setViewOnly(false);

      try {
        session = await withRetryOnConflict(() => startRemoteSession(screenId));
        if (cancelled) {
          void stopRemoteSession(screenId, session?.sessionId);
          return;
        }

        setState("connecting");
        setMessage("Agente listo. Estableciendo canal VNC...");

        const { default: RFB } = await import("@novnc/novnc/lib/rfb");
        if (cancelled || !containerRef.current) return;

        // Logs internos de noVNC en la consola del navegador (F12): el handshake RFB
        // informa aquí del motivo exacto cuando el servidor rechaza la sesión.
        try {
          const logging = await import("@novnc/novnc/lib/util/logging");
          logging.initLogging(process.env.NODE_ENV === "production" ? "warn" : "debug");
        } catch {
          // Sin el módulo de logging se sigue usando noVNC normalmente.
        }

        const socketUrl = resolveVncSocketUrl(session);
        console.info(`[VNC] Sesión ${session.sessionId} → ${socketUrl}`);
        if (session.webSocketUrl !== socketUrl) {
          console.info(`[VNC] URL del túnel normalizada (el servidor anunció ${session.webSocketUrl})`);
        }
        if (window.location.protocol === "https:" && socketUrl.startsWith("ws://")) {
          console.error("[VNC] El panel se sirve por HTTPS y el túnel es ws://: el navegador lo bloqueará.");
        }

        // El evento "disconnect" de noVNC solo informa de "clean"; el código y el motivo
        // del cierre solo están en el WebSocket, así que se crea aquí para poder registrarlos.
        let lastClose: { code: number; reason: string; clean: boolean } | null = null;
        const channel = new WebSocket(socketUrl);
        channel.binaryType = "arraybuffer";
        channel.addEventListener("open", () => console.info("[VNC] WebSocket del túnel abierto"));
        channel.addEventListener("error", () =>
          console.error(`[VNC] Error de WebSocket al conectar con ${socketUrl}`)
        );
        channel.addEventListener("close", (event) => {
          lastClose = { code: event.code, reason: event.reason, clean: event.wasClean };
          const detail = `código ${event.code}${event.reason ? `, motivo: ${event.reason}` : ""}`;
          if (event.wasClean) {
            console.info(`[VNC] WebSocket cerrado (${detail})`);
          } else {
            console.error(`[VNC] WebSocket cerrado de forma anómala (${detail})`);
          }
        });

        rfb = new RFB(containerRef.current, channel, {
          shared: true,
          credentials: { password: session.password },
        });

        // Fluidez: escala al contenedor, sin recortes y con compresión baja.
        rfb.scaleViewport = true;
        rfb.clipViewport = false;
        rfb.resizeSession = false;
        rfb.qualityLevel = 6;
        rfb.compressionLevel = 1;
        rfb.showDotCursor = true;
        rfb.focusOnClick = true;
        rfb.viewOnly = false;

        rfbRef.current = rfb;

        rfb.addEventListener("connect", () => {
          if (cancelled) return;
          console.info("[VNC] Sesión VNC establecida");
          setState("connected");
          setMessage("Haz clic sobre la pantalla para capturar el ratón y el teclado.");
          rfb?.focus();
          onSuccessRef.current(
            "Control Total",
            `Escritorio remoto de [${screenId}] conectado.`
          );
        });

        rfb.addEventListener("disconnect", (event: Event) => {
          if (cancelled) return;
          const detail = (event as CustomEvent<{ clean?: boolean }>).detail;
          console.error("[VNC] Desconexión", { clean: detail?.clean, close: lastClose });

          const reason = lastClose?.reason?.trim()
            ? ` Motivo: ${lastClose.reason.trim()}.`
            : lastClose && lastClose.code !== 1000 && lastClose.code !== 1005
              ? ` El túnel se cerró con el código ${lastClose.code}.`
              : "";

          setState("error");
          setMessage(
            detail?.clean
              ? "La sesión de control remoto se cerró."
              : `Se perdió la conexión con el escritorio remoto.${reason} Revisa la consola (F12) para el detalle de noVNC.`
          );
          onErrorRef.current("Control Total", `La sesión con [${screenId}] finalizó.`);
        });

        rfb.addEventListener("securityfailure", (event: Event) => {
          if (cancelled) return;
          const detail = (event as CustomEvent<{ status?: number }>).detail;
          console.error("[VNC] noVNC rechazó la negociación de seguridad", detail);
          setState("error");
          setMessage(
            "El servidor VNC rechazó la autenticación de la sesión. Comprueba en la mini-PC que TightVNC " +
              "aplique la contraseña de la sesión (un servicio TightVNC activo o una instancia anterior " +
              "abierta usan otra contraseña)."
          );
        });

        // noVNC pide credenciales si el servidor no las aceptó en el primer paso.
        rfb.addEventListener("credentialsrequired", (event: Event) => {
          if (cancelled) return;
          const detail = (event as CustomEvent<{ types?: string[] }>).detail;
          console.warn("[VNC] noVNC solicitó credenciales:", detail?.types);
          if (session && rfb) {
            rfb.sendCredentials({ password: session.password });
          }
        });

        // noVNC no expone un evento de error de red previo al cierre: el socket lo informa.
        requestAnimationFrame(() => {
          if (!cancelled && rfb && rfbRef.current === rfb) {
            setMessage("Autenticando con el servidor VNC...");
          }
        });
      } catch (err: unknown) {
        if (cancelled) return;
        const detail = err instanceof Error ? err.message : "Error desconocido";
        setState("error");
        setMessage(detail);
        onErrorRef.current("Control Total", detail);
      }
    };

    void run();

    return () => {
      cancelled = true;
      try {
        rfb?.disconnect();
      } catch {
        // El socket ya estaba cerrado.
      }
      rfbRef.current = null;
      if (session) {
        void stopRemoteSession(screenId, session.sessionId);
      }
    };
  }, [open, screenId, attempt]);

  // Estado del modo solo-lectura (se aplica en caliente, sin reconectar)
  useEffect(() => {
    if (rfbRef.current) {
      rfbRef.current.viewOnly = viewOnly;
    }
  }, [viewOnly]);

  // Sincroniza el estado del botón de pantalla completa con el navegador
  useEffect(() => {
    const handler = () => setIsFullscreen(Boolean(document.fullscreenElement));
    document.addEventListener("fullscreenchange", handler);
    return () => document.removeEventListener("fullscreenchange", handler);
  }, []);

  const toggleFullscreen = useCallback(async () => {
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await wrapperRef.current?.requestFullscreen();
      }
    } catch {
      // Algunos navegadores bloquean la API sin gesto de usuario.
    }
  }, []);

  const sendCtrlAltDel = useCallback(() => {
    rfbRef.current?.sendCtrlAltDel();
  }, []);

  const handleClose = useCallback(() => {
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => undefined);
    }
    onClose();
  }, [onClose]);

  if (!open) {
    return null;
  }

  const isWorking = state === "starting" || state === "connecting";

  return (
    <div
      ref={wrapperRef}
      className="fixed inset-0 z-[70] bg-black/95 backdrop-blur-sm flex flex-col"
      role="dialog"
      aria-modal="true"
      aria-label={`Control remoto de ${screenName || screenId}`}
    >
      {/* ─── Barra de herramientas ─────────────────────────────────────────── */}
      <div className="flex items-center justify-between gap-3 px-4 py-2.5 bg-zinc-950/90 border-b border-zinc-800">
        <div className="flex items-center gap-2.5 min-w-0">
          <MonitorSmartphone className="w-4 h-4 text-indigo-400 flex-shrink-0" />
          <div className="min-w-0">
            <p className="text-sm font-semibold text-white truncate">
              {screenName || screenId}
            </p>
            <p className="text-[10px] font-mono text-zinc-500 truncate">
              {screenId} · VNC · {STATE_LABEL[state]}
            </p>
          </div>
          <span
            className={`ml-1 flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[10px] font-semibold border ${
              state === "connected"
                ? "bg-emerald-500/15 border-emerald-500/30 text-emerald-400"
                : state === "error"
                  ? "bg-rose-500/15 border-rose-500/30 text-rose-300"
                  : "bg-amber-500/15 border-amber-500/30 text-amber-300"
            }`}
          >
            <span
              className={`w-1.5 h-1.5 rounded-full ${
                state === "connected"
                  ? "bg-emerald-400 animate-pulse"
                  : state === "error"
                    ? "bg-rose-400"
                    : "bg-amber-400 animate-pulse"
              }`}
            />
            {STATE_LABEL[state].toUpperCase()}
          </span>
        </div>

        <div className="flex items-center gap-1.5">
          <button
            onClick={() => setViewOnly((v) => !v)}
            disabled={state !== "connected"}
            title={viewOnly ? "Solo lectura activa (clic para controlar)" : "Activar solo lectura"}
            className={`flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-[11px] font-medium border transition-colors disabled:opacity-40 ${
              viewOnly
                ? "bg-amber-500/20 border-amber-500/40 text-amber-200"
                : "bg-zinc-900 border-zinc-700 text-zinc-300 hover:bg-zinc-800"
            }`}
          >
            <Eye className="w-3.5 h-3.5" />
            <span className="hidden sm:inline">{viewOnly ? "Solo ver" : "Control"}</span>
          </button>

          <button
            onClick={sendCtrlAltDel}
            disabled={state !== "connected"}
            title="Enviar Ctrl+Alt+Del"
            className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-[11px] font-medium bg-zinc-900 border border-zinc-700 text-zinc-300 hover:bg-zinc-800 transition-colors disabled:opacity-40"
          >
            <Keyboard className="w-3.5 h-3.5" />
            <span className="hidden sm:inline">Ctrl+Alt+Del</span>
          </button>

          <button
            onClick={toggleFullscreen}
            title={isFullscreen ? "Salir de pantalla completa" : "Pantalla completa"}
            className="p-1.5 rounded-lg bg-zinc-900 border border-zinc-700 text-zinc-300 hover:bg-zinc-800 transition-colors"
          >
            {isFullscreen ? <Minimize2 className="w-3.5 h-3.5" /> : <Maximize2 className="w-3.5 h-3.5" />}
          </button>

          <button
            onClick={handleClose}
            title="Cerrar control remoto"
            className="p-1.5 rounded-lg bg-zinc-900 border border-zinc-700 text-zinc-300 hover:bg-rose-600 hover:text-white transition-colors"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      </div>

      {/* ─── Lienzo noVNC ──────────────────────────────────────────────────── */}
      <div className="relative flex-1 min-h-0 bg-black overflow-hidden">
        <div
          ref={containerRef}
          className="w-full h-full flex items-center justify-center [&>canvas]:outline-none [&>canvas]:cursor-crosshair"
        />

        {state !== "connected" && (
          <div className="absolute inset-0 flex items-center justify-center bg-black/85 p-6">
            <div className="max-w-md w-full text-center flex flex-col items-center gap-3 rounded-2xl border border-zinc-800 bg-zinc-950/90 p-6 shadow-2xl">
              {state === "error" ? (
                <AlertTriangle className="w-9 h-9 text-rose-500" />
              ) : (
                <Loader2 className="w-9 h-9 text-indigo-400 animate-spin" />
              )}

              <p className="text-sm font-semibold text-white">
                {state === "error" ? "No se pudo abrir el escritorio" : "Preparando el escritorio remoto"}
              </p>
              <p className="text-xs text-zinc-400 leading-relaxed">{message}</p>

              {isWorking && (
                <p className="text-[11px] text-zinc-500 leading-relaxed">
                  El agente inicia su servidor VNC y abre el túnel hacia el servidor.
                  Suele tardar unos segundos.
                </p>
              )}

              <div className="flex items-center gap-2 pt-1">
                {state === "error" && (
                  <button
                    onClick={() => setAttempt((a) => a + 1)}
                    className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium bg-indigo-600 hover:bg-indigo-500 text-white transition-colors"
                  >
                    <RefreshCw className="w-3.5 h-3.5" />
                    Reintentar
                  </button>
                )}
                <button
                  onClick={handleClose}
                  className="px-3 py-1.5 rounded-lg text-xs font-medium bg-zinc-800 hover:bg-zinc-700 text-zinc-200 transition-colors"
                >
                  {state === "error" ? "Cerrar" : "Cancelar"}
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {/* ─── Pie de ayuda ──────────────────────────────────────────────────── */}
      <div className="px-4 py-2 bg-zinc-950/90 border-t border-zinc-800 text-[11px] text-zinc-500 flex items-center justify-between gap-3">
        <span className="truncate">
          Clic en la pantalla para capturar teclado y ratón. La calidad se adapta al ancho de banda.
        </span>
        <span className="font-mono hidden sm:inline">{message}</span>
      </div>
    </div>
  );
}

/**
 * El cierre de una sesión anterior puede tardar unos milisegundos en el servidor.
 * Si el arranque choca con un 409, se reintenta una vez.
 */
async function withRetryOnConflict(factory: () => Promise<RemoteSession>): Promise<RemoteSession> {
  try {
    return await factory();
  } catch (err) {
    const message = err instanceof Error ? err.message : "";
    if (!message.includes("409") && !message.toLowerCase().includes("ya tiene una sesión")) {
      throw err;
    }

    await new Promise((resolve) => setTimeout(resolve, 700));
    return factory();
  }
}
