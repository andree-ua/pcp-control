"use client";

import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import {
  AlertTriangle,
  Cast,
  CheckCircle2,
  Clock,
  Gauge,
  Loader2,
  MonitorPlay,
  Radio,
  RefreshCw,
  ScreenShare,
  Square,
  Volume2,
  X,
} from "lucide-react";
import {
  notifyProjectorLive,
  startProjectorSession,
  stopProjectorSession,
} from "@/services/api";
import type { ProjectorSession } from "@/types/projector";

interface ProjectorModalProps {
  open: boolean;
  screenId: string;
  screenName?: string;
  onClose: () => void;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

type Phase = "preparing" | "ready" | "capturing" | "connecting" | "live" | "error";

const PHASE_LABEL: Record<Phase, string> = {
  preparing: "Preparando sesión",
  ready: "Listo para emitir",
  capturing: "Selecciona la pantalla",
  connecting: "Conectando",
  live: "En directo",
  error: "Emisión detenida",
};

const QUALITY_OPTIONS = [
  { id: "1080p30", label: "1080p · 30 fps", width: 1920, height: 1080, fps: 30 },
  { id: "1080p60", label: "1080p · 60 fps", width: 1920, height: 1080, fps: 60 },
  { id: "720p30", label: "720p · 30 fps (menos ancho de banda)", width: 1280, height: 720, fps: 30 },
] as const;

type QualityId = (typeof QUALITY_OPTIONS)[number]["id"];

interface WhipPublisher {
  pc: RTCPeerConnection;
  resourceUrl: string;
  token: string;
}

/**
 * Modal de proyector (Bloque 5): captura la pantalla del operador con
 * getDisplayMedia y la publica a MediaMTX por WHIP. El servidor ordena al agente
 * reproducir el stream con caché baja.
 */
export function ProjectorModal({
  open,
  screenId,
  screenName,
  onClose,
  onSuccess,
  onError,
}: ProjectorModalProps) {
  const [phase, setPhase] = useState<Phase>("preparing");
  const [message, setMessage] = useState("Preparando la sesión de proyección...");
  const [session, setSession] = useState<ProjectorSession | null>(null);
  const [previewStream, setPreviewStream] = useState<MediaStream | null>(null);
  const [quality, setQuality] = useState<QualityId>("1080p30");
  const [shareAudio, setShareAudio] = useState(true);
  const [elapsed, setElapsed] = useState(0);
  const [bitrateKbps, setBitrateKbps] = useState(0);
  const [attempt, setAttempt] = useState(0);

  const sessionRef = useRef<ProjectorSession | null>(null);
  const whipRef = useRef<WhipPublisher | null>(null);
  const previewTrackRef = useRef<MediaStream | null>(null);
  const previewElRef = useRef<HTMLVideoElement | null>(null);
  const startedAtRef = useRef(0);
  const stoppedRef = useRef(false);

  const onCloseRef = useRef(onClose);
  const onSuccessRef = useRef(onSuccess);
  const onErrorRef = useRef(onError);

  useEffect(() => {
    onCloseRef.current = onClose;
    onSuccessRef.current = onSuccess;
    onErrorRef.current = onError;
  }, [onClose, onSuccess, onError]);

  // Bloquea el scroll del panel mientras el modal está abierto.
  useEffect(() => {
    if (!open) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [open]);

  /**
   * Libera todo: publicador WHIP, captura local y sesión del servidor.
   * Es idempotente porque cada recurso se null-ea antes de soltarlo.
   */
  const teardown = useCallback(async () => {
    stoppedRef.current = true;

    const publisher = whipRef.current;
    whipRef.current = null;

    if (publisher) {
      try {
        publisher.pc.close();
      } catch {
        // La conexión ya estaba cerrada.
      }

      try {
        await fetch(publisher.resourceUrl, {
          method: "DELETE",
          keepalive: true,
          headers: { Authorization: `Bearer ${publisher.token}` },
        });
      } catch {
        // MediaMTX cierra la publicación al caer la conexión.
      }
    }

    const localStream = previewTrackRef.current;
    previewTrackRef.current = null;
    localStream?.getTracks().forEach((track) => track.stop());

    if (previewElRef.current) {
      previewElRef.current.srcObject = null;
    }

    setPreviewStream(null);

    const activeSession = sessionRef.current;
    sessionRef.current = null;
    if (activeSession) {
      await stopProjectorSession(screenId, activeSession.sessionId);
    }
  }, [screenId]);

  // Cierre del modal (incluido el desmontaje): siempre libera la emisión.
  useEffect(() => {
    if (!open) return;
    return () => {
      void teardown();
    };
  }, [open, teardown]);

  // Apertura: crea una sesión nueva en el servidor.
  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    stoppedRef.current = false;
    sessionRef.current = null;

    setSession(null);
    setPhase("preparing");
    setMessage("Preparando la sesión de proyección...");
    setPreviewStream(null);
    setElapsed(0);
    setBitrateKbps(0);

    const loadSession = async (): Promise<ProjectorSession> => {
      try {
        return await startProjectorSession(screenId);
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : "";
        // Una sesión huérfana de un intento anterior se cierra y se reintenta una vez.
        if (!msg.includes("409") && !msg.toLowerCase().includes("ya tiene una emisión")) {
          throw err;
        }

        await stopProjectorSession(screenId);
        await new Promise((resolve) => setTimeout(resolve, 600));
        return startProjectorSession(screenId);
      }
    };

    const run = async () => {
      try {
        const created = await loadSession();
        if (cancelled || stoppedRef.current) {
          void stopProjectorSession(screenId, created.sessionId);
          return;
        }

        sessionRef.current = created;
        setSession(created);
        setPhase("ready");
        setMessage(
          "Pulsa «Compartir pantalla» y elige la ventana o la pantalla que quieres enviar a la TV."
        );

        if (
          typeof window !== "undefined" &&
          window.location.protocol === "https:" &&
          created.whipUrl.startsWith("http:")
        ) {
          setMessage(
            "Atención: el panel usa HTTPS y MediaMTX está configurado en HTTP. El navegador bloqueará la publicación (contenido mixto). Usa un dominio HTTPS para MediaMTX."
          );
        }
      } catch (err: unknown) {
        if (cancelled) return;
        const detail = err instanceof Error ? err.message : "Error desconocido";
        setPhase("error");
        setMessage(detail);
        onErrorRef.current("Proyector", detail);
      }
    };

    void run();

    return () => {
      cancelled = true;
    };
  }, [open, screenId, attempt]);

  // El elemento <video> recibe el MediaStream de la captura local.
  useEffect(() => {
    if (previewElRef.current) {
      previewElRef.current.srcObject = previewStream;
    }
  }, [previewStream]);

  // Cronómetro de la emisión.
  useEffect(() => {
    if (phase !== "live") return;

    startedAtRef.current = Date.now();
    setElapsed(0);

    const timer = setInterval(() => {
      setElapsed(Math.floor((Date.now() - startedAtRef.current) / 1000));
    }, 1000);

    return () => clearInterval(timer);
  }, [phase]);

  // Bitrate real de salida (outbound-rtp) cada 2 s.
  useEffect(() => {
    if (phase !== "live") return;

    let lastBytes = 0;
    let lastTimestamp = performance.now();

    const timer = setInterval(() => {
      const pc = whipRef.current?.pc;
      if (!pc) return;

      void pc.getStats().then((stats) => {
        stats.forEach((report) => {
          const outbound = report as {
            type?: string;
            kind?: string;
            bytesSent?: number;
          };

          if (outbound.type !== "outbound-rtp" || outbound.kind !== "video") return;
          if (typeof outbound.bytesSent !== "number") return;

          const now = performance.now();
          const deltaMs = Math.max(1, now - lastTimestamp);
          const kbps = Math.round(((outbound.bytesSent - lastBytes) * 8) / deltaMs);
          lastBytes = outbound.bytesSent;
          lastTimestamp = now;
          setBitrateKbps(Math.max(0, kbps));
        });
      }).catch(() => undefined);
    }, 2000);

    return () => clearInterval(timer);
  }, [phase]);

  /** Inicia la captura y publica por WHIP; al confirmar, ordena la pantalla. */
  const handleStart = useCallback(async () => {
    if (!session) return;

    const option = QUALITY_OPTIONS.find((q) => q.id === quality) ?? QUALITY_OPTIONS[0];

    if (
      typeof navigator === "undefined" ||
      !navigator.mediaDevices ||
      typeof navigator.mediaDevices.getDisplayMedia !== "function"
    ) {
      setPhase("error");
      setMessage(
        "Este navegador no permite compartir pantalla en este contexto. Abre el panel por HTTPS o desde localhost."
      );
      return;
    }

    setPhase("capturing");
    setMessage("Selecciona la pantalla o la ventana que quieres proyectar...");

    let display: MediaStream | null = null;

    try {
      display = await requestDisplayStream(option, shareAudio);
      if (stoppedRef.current) {
        display.getTracks().forEach((track) => track.stop());
        return;
      }

      previewTrackRef.current = display;
      setPreviewStream(display);

      const videoTrack = display.getVideoTracks()[0];
      videoTrack?.addEventListener("ended", () => {
        void teardown().then(() => {
          setPhase("error");
          setMessage("Dejaste de compartir la pantalla. La emisión a la TV se detuvo.");
          onErrorRef.current("Proyector", `La emisión a [${screenId}] se detuvo.`);
        });
      });

      setPhase("connecting");
      setMessage("Publicando en MediaMTX por WHIP...");

      const publisher = await publishWhip(session.whipUrl, session.whipToken, display);
      whipRef.current = publisher;

      if (stoppedRef.current) {
        await teardown();
        return;
      }

      setMessage("MediaMTX recibe la señal. Conectando la pantalla...");
      await notifyProjectorLive(screenId, session.sessionId);

      if (stoppedRef.current) return;

      setPhase("live");
      setMessage(`[${screenId}] está mostrando la emisión.`);
      onSuccessRef.current("Proyector", `Emisión enviada a [${screenId}].`);
    } catch (err: unknown) {
      const detail = describeError(err);
      await teardown();
      setPhase("error");
      setMessage(detail);
      onErrorRef.current("Proyector", detail);
    }
  }, [session, quality, shareAudio, screenId, teardown]);

  /** Finaliza la emisión a petición del operador y cierra el modal. */
  const handleStop = useCallback(async () => {
    const wasLive = phase === "live";
    await teardown();
    if (wasLive) {
      onSuccessRef.current("Proyector", `Emisión de [${screenId}] finalizada.`);
    }
    onCloseRef.current();
  }, [phase, screenId]);

  const handleClose = useCallback(() => {
    void handleStop();
  }, [handleStop]);

  if (!open) {
    return null;
  }

  const isWorking = phase === "preparing" || phase === "capturing" || phase === "connecting";
  const selectedQuality = QUALITY_OPTIONS.find((q) => q.id === quality) ?? QUALITY_OPTIONS[0];

  return (
    <div
      className="fixed inset-0 z-[70] bg-black/95 backdrop-blur-sm flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-label={`Proyector de ${screenName || screenId}`}
    >
      <div className="w-full max-w-5xl rounded-2xl border border-zinc-800 bg-zinc-950 shadow-2xl overflow-hidden flex flex-col max-h-[92vh]">
        {/* ─── Barra de título ────────────────────────────────────────────── */}
        <div className="flex items-center justify-between gap-3 px-4 py-3 bg-zinc-900/80 border-b border-zinc-800">
          <div className="flex items-center gap-3 min-w-0">
            <div className="w-9 h-9 rounded-xl bg-gradient-to-tr from-emerald-600 to-teal-500 flex items-center justify-center shadow-lg shadow-emerald-950/50">
              <Cast className="w-4 h-4 text-white" />
            </div>
            <div className="min-w-0">
              <p className="text-sm font-semibold text-white truncate">
                Proyector · {screenName || screenId}
              </p>
              <p className="text-[10px] font-mono text-zinc-500 truncate">
                {screenId} · WHIP → MediaMTX → RTSP
              </p>
            </div>
            <span
              className={`ml-1 flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[10px] font-semibold border ${
                phase === "live"
                  ? "bg-rose-500/15 border-rose-500/30 text-rose-300"
                  : phase === "error"
                    ? "bg-rose-500/15 border-rose-500/30 text-rose-300"
                    : "bg-amber-500/15 border-amber-500/30 text-amber-300"
              }`}
            >
              <span
                className={`w-1.5 h-1.5 rounded-full ${
                  phase === "live"
                    ? "bg-rose-400 animate-pulse"
                    : phase === "error"
                      ? "bg-rose-400"
                      : "bg-amber-400 animate-pulse"
                }`}
              />
              {PHASE_LABEL[phase].toUpperCase()}
            </span>
          </div>

          <button
            onClick={handleClose}
            title="Cerrar proyector"
            className="p-1.5 rounded-lg bg-zinc-900 border border-zinc-700 text-zinc-300 hover:bg-rose-600 hover:text-white transition-colors"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="flex-1 min-h-0 overflow-y-auto p-4 grid gap-4 lg:grid-cols-[minmax(0,1.6fr)_minmax(280px,1fr)]">
          {/* ─── Vista previa local ──────────────────────────────────────── */}
          <div className="space-y-3">
            <div className="relative aspect-video w-full rounded-xl overflow-hidden bg-black border border-zinc-800">
              <video
                ref={previewElRef}
                autoPlay
                muted
                playsInline
                className={`w-full h-full object-contain ${previewStream ? "" : "hidden"}`}
              />

              {!previewStream && (
                <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-zinc-500 p-6 text-center">
                  {phase === "error" ? (
                    <AlertTriangle className="w-10 h-10 text-rose-500" />
                  ) : isWorking ? (
                    <Loader2 className="w-10 h-10 text-emerald-400 animate-spin" />
                  ) : (
                    <ScreenShare className="w-10 h-10 text-zinc-600" />
                  )}
                  <p className="text-xs text-zinc-400 max-w-sm leading-relaxed">{message}</p>
                </div>
              )}

              {phase === "live" && (
                <div className="absolute top-2.5 left-2.5 flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[10px] font-semibold backdrop-blur-md border bg-rose-600/80 border-rose-400/40 text-white shadow-lg">
                  <span className="w-2 h-2 rounded-full bg-white animate-pulse" />
                  EN DIRECTO · {formatDuration(elapsed)}
                </div>
              )}
            </div>

            <div className="grid grid-cols-3 gap-2">
              <StatCard icon={<Clock className="w-3.5 h-3.5" />} label="Tiempo" value={formatDuration(elapsed)} />
              <StatCard
                icon={<Gauge className="w-3.5 h-3.5" />}
                label="Bitrate"
                value={bitrateKbps > 0 ? `${bitrateKbps} kbps` : "—"}
              />
              <StatCard
                icon={<Radio className="w-3.5 h-3.5" />}
                label="Señal"
                value={phase === "live" ? "Publicando" : "Inactiva"}
              />
            </div>

            {session && (
              <div className="rounded-xl border border-zinc-800 bg-zinc-900/50 p-3 text-[11px] text-zinc-400 space-y-1.5">
                <p className="flex items-center justify-between gap-2">
                  <span className="text-zinc-500">Stream</span>
                  <span className="font-mono text-zinc-300 truncate">{session.streamPath}</span>
                </p>
                <p className="flex items-center justify-between gap-2">
                  <span className="text-zinc-500">Caché del agente</span>
                  <span className="font-mono text-zinc-300">{session.bufferMs} ms</span>
                </p>
                <p className="flex items-center justify-between gap-2">
                  <span className="text-zinc-500">Volumen TV</span>
                  <span className="font-mono text-zinc-300 flex items-center gap-1">
                    <Volume2 className="w-3 h-3" /> {session.volume}%
                  </span>
                </p>
              </div>
            )}
          </div>

          {/* ─── Controles y estado ──────────────────────────────────────── */}
          <div className="flex flex-col gap-3">
            <div className="rounded-xl border border-zinc-800 bg-zinc-900/50 p-3 space-y-3">
              <div>
                <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
                  Calidad de captura
                </label>
                <select
                  value={quality}
                  onChange={(e) => setQuality(e.target.value as QualityId)}
                  disabled={phase !== "ready" && phase !== "error"}
                  className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2.5 py-1.5 text-zinc-200 text-[11px] focus:outline-none focus:border-emerald-500 disabled:opacity-40"
                >
                  {QUALITY_OPTIONS.map((option) => (
                    <option key={option.id} value={option.id}>
                      {option.label}
                    </option>
                  ))}
                </select>
                <p className="text-[10px] text-zinc-600 mt-1">
                  Se envía a {selectedQuality.width}×{selectedQuality.height} y {selectedQuality.fps} fps.
                </p>
              </div>

              <label className="flex items-center justify-between gap-2 text-[11px] text-zinc-300">
                <span className="flex items-center gap-1.5">
                  <Volume2 className="w-3.5 h-3.5 text-zinc-400" />
                  Compartir audio del sistema
                </span>
                <input
                  type="checkbox"
                  checked={shareAudio}
                  onChange={(e) => setShareAudio(e.target.checked)}
                  disabled={phase !== "ready" && phase !== "error"}
                  className="accent-emerald-500"
                />
              </label>
            </div>

            <div
              className={`rounded-xl border p-3 text-[11px] leading-relaxed ${
                phase === "error"
                  ? "border-rose-500/30 bg-rose-500/5 text-rose-200"
                  : phase === "live"
                    ? "border-emerald-500/30 bg-emerald-500/5 text-emerald-200"
                    : "border-zinc-800 bg-zinc-900/50 text-zinc-400"
              }`}
            >
              <p className="flex items-start gap-2">
                {phase === "live" ? (
                  <CheckCircle2 className="w-4 h-4 mt-0.5 flex-shrink-0" />
                ) : phase === "error" ? (
                  <AlertTriangle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                ) : (
                  <MonitorPlay className="w-4 h-4 mt-0.5 flex-shrink-0" />
                )}
                <span>{message}</span>
              </p>
            </div>

            <div className="mt-auto flex flex-col gap-2">
              {(phase === "ready" || phase === "error") && (
                <button
                  onClick={phase === "error" ? () => setAttempt((value) => value + 1) : handleStart}
                  className="w-full flex items-center justify-center gap-2 py-2.5 px-3 rounded-xl font-semibold text-xs bg-gradient-to-r from-emerald-600 to-teal-600 hover:from-emerald-500 hover:to-teal-500 text-white transition-all active:scale-[0.98] border border-emerald-400/40 shadow-lg shadow-emerald-950/50"
                >
                  {phase === "error" ? (
                    <>
                      <RefreshCw className="w-4 h-4" /> Reintentar
                    </>
                  ) : (
                    <>
                      <ScreenShare className="w-4 h-4" /> Compartir pantalla
                    </>
                  )}
                </button>
              )}

              {isWorking && (
                <button
                  disabled
                  className="w-full flex items-center justify-center gap-2 py-2.5 px-3 rounded-xl font-semibold text-xs bg-zinc-800 text-zinc-300 border border-zinc-700"
                >
                  <Loader2 className="w-4 h-4 animate-spin" />
                  {PHASE_LABEL[phase]}...
                </button>
              )}

              {phase === "live" && (
                <button
                  onClick={handleClose}
                  className="w-full flex items-center justify-center gap-2 py-2.5 px-3 rounded-xl font-semibold text-xs bg-rose-600 hover:bg-rose-500 text-white transition-all active:scale-[0.98] border border-rose-400/40 shadow-lg shadow-rose-950/50"
                >
                  <Square className="w-4 h-4 fill-current" /> Finalizar emisión
                </button>
              )}

              <button
                onClick={handleClose}
                className="w-full flex items-center justify-center gap-2 py-2 px-3 rounded-xl font-medium text-[11px] bg-zinc-900 hover:bg-zinc-800 text-zinc-300 border border-zinc-700 transition-colors"
              >
                Cancelar
              </button>
            </div>
          </div>
        </div>

        <div className="px-4 py-2 bg-zinc-950 border-t border-zinc-800 text-[10px] text-zinc-500 flex items-center justify-between gap-3">
          <span className="truncate">
            La TV recibe la señal por RTSP desde MediaMTX. Cerrar esta ventana detiene la emisión.
          </span>
          <span className="font-mono hidden sm:inline">PC → WHIP → MediaMTX → RTSP</span>
        </div>
      </div>
    </div>
  );
}

function StatCard({
  icon,
  label,
  value,
}: {
  icon: ReactNode;
  label: string;
  value: string;
}) {
  return (
    <div className="rounded-lg border border-zinc-800/70 bg-zinc-900/50 px-2.5 py-2">
      <p className="flex items-center gap-1 text-[9px] uppercase tracking-wider text-zinc-500 font-semibold">
        {icon}
        {label}
      </p>
      <p className="text-[11px] font-mono text-zinc-200 mt-0.5 truncate">{value}</p>
    </div>
  );
}

/**
 * Captura la pantalla. Si el navegador no acepta audio en getDisplayMedia
 * (algunos equipos solo comparten vídeo), se reintenta sin audio.
 */
async function requestDisplayStream(
  option: (typeof QUALITY_OPTIONS)[number],
  withAudio: boolean
): Promise<MediaStream> {
  const video = {
    frameRate: { ideal: option.fps, max: option.fps },
    width: { ideal: option.width },
    height: { ideal: option.height },
  };

  try {
    return await navigator.mediaDevices.getDisplayMedia({ video, audio: withAudio });
  } catch (err: unknown) {
    const name = err instanceof DOMException ? err.name : "";
    if (!withAudio || (name !== "TypeError" && name !== "NotSupportedError")) {
      throw err;
    }

    return navigator.mediaDevices.getDisplayMedia({ video, audio: false });
  }
}

/** Publica un MediaStream con el protocolo WHIP y devuelve el recurso para el DELETE. */
async function publishWhip(
  whipUrl: string,
  token: string,
  stream: MediaStream
): Promise<WhipPublisher> {
  const pc = new RTCPeerConnection({ iceServers: [] });

  try {
    for (const track of stream.getTracks()) {
      pc.addTrack(track, stream);
    }

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await waitForIceGathering(pc, 3000);

    const local = pc.localDescription;
    if (!local) {
      throw new Error("El navegador no generó la oferta SDP para publicar.");
    }

    // Modo no-trickle: la oferta viaja con los candidatos ya recolectados, que es
    // lo que MediaMTX espera en un POST WHIP con Content-Type application/sdp.
    // El token va en el header (método documentado para WebRTC) y en la query.
    const response = await fetch(whipUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/sdp",
        Accept: "application/sdp",
        Authorization: `Bearer ${token}`,
      },
      body: local.sdp,
    });

    if (!response.ok) {
      const detail = await response.text().catch(() => "");
      throw new Error(
        `MediaMTX rechazó la publicación (HTTP ${response.status})` +
          (detail ? `: ${detail.slice(0, 200)}` : "")
      );
    }

    const answer = await response.text();
    const location = response.headers.get("Location");
    let resourceUrl = whipUrl;

    if (location) {
      const resolved = new URL(location, whipUrl);
      // Algunas versiones devuelven una ruta relativa sin el token de la query.
      // Se conserva el token para que el DELETE de cierre también quede autorizado.
      if (!resolved.search) {
        resolved.search = new URL(whipUrl).search;
      }
      resourceUrl = resolved.toString();
    }

    await pc.setRemoteDescription({ type: "answer", sdp: answer });

    return { pc, resourceUrl, token };
  } catch (err) {
    try {
      pc.close();
    } catch {
      // La conexión no llegó a abrirse.
    }
    throw err;
  }
}

function waitForIceGathering(pc: RTCPeerConnection, timeoutMs: number): Promise<void> {
  if (pc.iceGatheringState === "complete") {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    let settled = false;

    const finish = () => {
      if (settled) return;
      settled = true;
      pc.removeEventListener("icegatheringstatechange", onChange);
      resolve();
    };

    const onChange = () => {
      if (pc.iceGatheringState === "complete") {
        finish();
      }
    };

    pc.addEventListener("icegatheringstatechange", onChange);
    setTimeout(finish, timeoutMs);
  });
}

function describeError(err: unknown): string {
  if (err instanceof DOMException) {
    if (err.name === "NotAllowedError") {
      return "El navegador bloqueó la captura de pantalla o no concediste el permiso. Vuelve a intentarlo y acepta compartir.";
    }
    if (err.name === "NotReadableError") {
      return "Windows no pudo leer la pantalla seleccionada. Cierra aplicaciones de captura en exclusiva (OBS, Teams) e inténtalo de nuevo.";
    }
    if (err.name === "AbortError") {
      return "La selección de pantalla se canceló antes de empezar.";
    }
  }

  return err instanceof Error ? err.message : "Error desconocido al iniciar el proyector.";
}

function formatDuration(totalSeconds: number): string {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}
