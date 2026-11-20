"use client";

import { useEffect, useState } from "react";
import { ScreenInfo } from "@/types/screen";
import { sendScreenCommand, wakeScreen } from "@/services/api";
import { useAuth } from "@/context/AuthContext";
import { FileExplorerModal } from "@/components/FileExplorerModal";
import { RemoteDesktopModal } from "@/components/RemoteDesktopModal";
import { SystemActionsMenu } from "@/components/SystemActionsMenu";
import {
  Play,
  Pause,
  Square,
  Cpu,
  HardDrive,
  Tv,
  Clock,
  Film,
  Volume2,
  ChevronDown,
  ChevronUp,
  Radio,
  AlertTriangle,
  FolderOpen,
  MonitorSmartphone,
  Power,
} from "lucide-react";

interface ScreenCardProps {
  screen: ScreenInfo;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

export function ScreenCard({ screen, onSuccess, onError }: ScreenCardProps) {
  const { isAdmin } = useAuth();
  const [loadingAction, setLoadingAction] = useState<string | null>(null);
  const [showOptions, setShowOptions] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showRemote, setShowRemote] = useState(false);
  const [customMedia, setCustomMedia] = useState("C:\\videos\\promo_verano.mp4");
  const [customVolume, setCustomVolume] = useState(screen.volume || 80);
  const [waking, setWaking] = useState(false);
  const [wakeCountdown, setWakeCountdown] = useState(0);

  const isOnline = screen.status.toLowerCase() === "online";
  const isPlaying = isOnline && screen.currentMedia && screen.currentMedia !== "idle";

  // La pantalla se despertó: se limpia el estado de espera sin esperar al timeout.
  useEffect(() => {
    if (isOnline && waking) {
      setWaking(false);
      setWakeCountdown(0);
    }
  }, [isOnline, waking]);

  // Cuenta atrás del Wake-on-LAN. Si expira, la pantalla no respondió.
  useEffect(() => {
    if (!waking) return;

    if (wakeCountdown <= 0) {
      setWaking(false);
      onError(
        "Sin respuesta",
        `La pantalla [${screen.id}] no respondió al Magic Packet. Revisa WOL en la BIOS y el Inicio Rápido de Windows.`
      );
      return;
    }

    const timer = setTimeout(() => setWakeCountdown((value) => value - 1), 1000);
    return () => clearTimeout(timer);
  }, [waking, wakeCountdown, screen.id, onError]);

  const canWake =
    isAdmin &&
    Boolean(screen.macAddress) &&
    screen.wolEnabled !== false &&
    screen.isWired !== false;

  const wakeHint = !isAdmin
    ? "Solo administradores"
    : !screen.macAddress
    ? "La pantalla aún no ha reportado su MAC: actualiza el agente para poder despertarla"
    : screen.wolEnabled === false
    ? "Wake-on-LAN deshabilitado para esta pantalla"
    : screen.isWired === false
    ? "Conectada por Wi-Fi: el Wake-on-LAN no funciona por inalámbrico"
    : "Enviar un Magic Packet para encender la pantalla";

  const handleCommand = async (type: string, payload?: Record<string, unknown>) => {
    if (!isAdmin) {
      onError("Permisos insuficientes", "Tu cuenta es Viewer: no puede enviar comandos.");
      return;
    }

    if (!isOnline) {
      onError("Pantalla Offline", `La pantalla [${screen.id}] está desconectada.`);
      return;
    }

    try {
      setLoadingAction(type);
      await sendScreenCommand(screen.id, { type, payload });
      onSuccess("Comando Enviado", `Orden '${type.toUpperCase()}' enviada a [${screen.id}].`);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Error al procesar el comando";
      onError("Fallo de Comando", msg);
    } finally {
      setLoadingAction(null);
    }
  };

  const handlePlay = () => {
    handleCommand("play", {
      mediaPath: customMedia.trim() || "C:\\videos\\promo_verano.mp4",
      loop: true,
      volume: customVolume,
      fitMode: "cover",
    });
  };

  const handlePause = () => {
    handleCommand("pause");
  };

  const handleStop = () => {
    handleCommand("stop");
  };

  const handleVolumeSubmit = () => {
    handleCommand("volume", { volume: customVolume });
  };

  const handleWake = async () => {
    if (!isAdmin) {
      onError("Permisos insuficientes", "Tu cuenta es Viewer: no puede encender pantallas.");
      return;
    }

    try {
      setLoadingAction("wake");
      const result = await wakeScreen(screen.id);

      setWaking(true);
      setWakeCountdown(45);

      onSuccess(
        "Magic Packet enviado",
        result.mode === "peer" && result.relayScreenId
          ? `Encendiendo [${screen.id}] a través de la pantalla ${result.relayScreenId}...`
          : `Encendiendo [${screen.id}] desde el servidor...`
      );
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Error al solicitar el encendido";
      onError("No se pudo encender", msg);
    } finally {
      setLoadingAction(null);
    }
  };

  // Formato de tiempo relativo simple
  const formatTimeAgo = (dateStr: string) => {
    if (!dateStr) return "Sin datos";
    const diff = Math.floor((new Date().getTime() - new Date(dateStr).getTime()) / 1000);
    if (diff < 5) return "hace unos segundos";
    if (diff < 60) return `hace ${diff}s`;
    const mins = Math.floor(diff / 60);
    if (mins < 60) return `hace ${mins}m`;
    return `hace ${Math.floor(mins / 60)}h`;
  };

  return (
    <div
      className={`group relative rounded-2xl border transition-all duration-300 shadow-md flex flex-col ${
        isOnline
          ? "bg-zinc-900/90 border-zinc-800 hover:border-zinc-700 hover:shadow-2xl hover:shadow-zinc-950/60"
          : "bg-zinc-950/70 border-zinc-900 opacity-80"
      }`}
    >
      {/* ─── 1. Monitor de TV / Vista Previa en Vivo ───────────────────────── */}
      <div className="p-4 pb-0">
        <div className="relative aspect-video w-full rounded-xl overflow-hidden bg-black border-2 border-zinc-800/80 shadow-inner flex items-center justify-center">
          {isOnline ? (
            screen.thumbnailBase64 ? (
              // Fotograma en vivo emitido por el agente
              <>
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  src={
                    screen.thumbnailBase64.startsWith("data:")
                      ? screen.thumbnailBase64
                      : `data:image/jpeg;base64,${screen.thumbnailBase64}`
                  }
                  alt={`En vivo ${screen.id}`}
                  className="w-full h-full object-cover select-none"
                />

                {/* Badge de transmisión en vivo sobre el frame */}
                <div className="absolute top-2.5 left-2.5 flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[10px] font-semibold backdrop-blur-md border shadow-lg bg-black/60 border-white/20 text-white">
                  <span
                    className={`w-2 h-2 rounded-full ${
                      isPlaying ? "bg-rose-500 animate-pulse" : "bg-emerald-400"
                    }`}
                  />
                  <span className="tracking-wider uppercase">
                    {isPlaying ? "EN VIVO" : "STANDBY"}
                  </span>
                </div>

                {/* Nombre de archivo sutil abajo a la derecha */}
                {isPlaying && (
                  <div className="absolute bottom-2 right-2 max-w-[70%] px-2 py-0.5 rounded bg-black/70 backdrop-blur-sm text-[10px] text-zinc-300 truncate font-mono border border-white/10">
                    {screen.currentMedia}
                  </div>
                )}
              </>
            ) : (
              // Pantalla activa esperando primer frame
              <div className="flex flex-col items-center justify-center text-zinc-500 gap-2 p-4 text-center">
                <Tv className="w-8 h-8 text-zinc-600 animate-pulse" />
                <p className="text-xs font-mono text-zinc-400">Conectado — Esperando fotograma...</p>
              </div>
            )
          ) : (
            // Pantalla Offline: Efecto "SIN SEÑAL" tipo monitor de TV
            <div className="w-full h-full relative overflow-hidden flex flex-col items-center justify-center bg-zinc-950">
              {/* Barras de calibración retro SMPTE atenuadas */}
              <div className="absolute inset-0 flex opacity-30">
                <div className="flex-1 bg-slate-200"></div>
                <div className="flex-1 bg-yellow-400"></div>
                <div className="flex-1 bg-cyan-400"></div>
                <div className="flex-1 bg-green-500"></div>
                <div className="flex-1 bg-fuchsia-500"></div>
                <div className="flex-1 bg-rose-600"></div>
                <div className="flex-1 bg-blue-700"></div>
              </div>

              {/* Scanlines / líneas horizontales CRT */}
              <div
                className="absolute inset-0 pointer-events-none opacity-20"
                style={{
                  backgroundImage: "linear-gradient(rgba(18, 16, 16, 0) 50%, rgba(0, 0, 0, 0.5) 50%)",
                  backgroundSize: "100% 4px",
                }}
              />

              {/* Cartel central "SIN SEÑAL" */}
              <div className="relative z-10 px-3.5 py-1.5 rounded-lg bg-black/90 border border-rose-500/60 shadow-2xl flex items-center gap-2">
                <AlertTriangle className="w-4 h-4 text-rose-500 animate-pulse" />
                <span className="font-mono text-xs font-bold tracking-widest text-rose-400">
                  SIN SEÑAL (OFFLINE)
                </span>
              </div>
              <p className="relative z-10 text-[10px] font-mono text-zinc-400 mt-1.5">
                Dispositivo no responde
              </p>
            </div>
          )}
        </div>
      </div>

      {/* ─── 2. Datos y Métricas de la Pantalla ────────────────────────────── */}
      <div className="p-5 pb-4 flex-1 flex flex-col justify-between">
        <div>
          {/* Cabecera de la tarjeta */}
          <div className="flex items-start justify-between gap-3">
            <div>
              <h3 className="font-semibold text-white text-base leading-snug group-hover:text-indigo-400 transition-colors">
                {screen.name || screen.id}
              </h3>
              <p className="text-xs font-mono text-zinc-400 mt-0.5">{screen.id}</p>
              {screen.macAddress && (
                <p
                  className="text-[10px] font-mono text-zinc-500 mt-0.5 truncate"
                  title={`MAC ${screen.macAddress}${screen.localIp ? ` · IP ${screen.localIp}` : ""}`}
                >
                  {screen.macAddress}
                  {screen.localIp ? ` · ${screen.localIp}` : ""}
                </p>
              )}
              {screen.groupName && (
                <span className="inline-flex items-center mt-1.5 px-2 py-0.5 rounded-full text-[10px] font-medium bg-sky-500/10 text-sky-300 border border-sky-500/25">
                  {screen.groupName}
                </span>
              )}
            </div>

            {/* Badge de Conexión */}
            <div
              className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold border ${
                isOnline
                  ? "bg-emerald-500/15 border-emerald-500/30 text-emerald-400"
                  : "bg-zinc-800/80 border-zinc-700/60 text-zinc-400"
              }`}
            >
              <span
                className={`w-2 h-2 rounded-full ${
                  isOnline ? "bg-emerald-400 animate-pulse" : "bg-zinc-500"
                }`}
              />
              <span className="uppercase tracking-wider text-[10px]">
                {isOnline ? "Online" : "Offline"}
              </span>
            </div>
          </div>

          {/* Estado de reproducción textual */}
          <div className="mt-3 p-2.5 rounded-xl bg-zinc-950/60 border border-zinc-800/80 flex items-center gap-2.5">
            <Film className={`w-4 h-4 flex-shrink-0 ${isPlaying ? "text-violet-400" : "text-zinc-500"}`} />
            <div className="flex-1 min-w-0">
              <p className="text-[10px] uppercase tracking-wider text-zinc-400 font-medium">Medio Actual</p>
              <p className="text-xs font-medium text-zinc-200 truncate" title={screen.currentMedia}>
                {isPlaying ? screen.currentMedia : "En reposo (idle)"}
              </p>
            </div>
            {isPlaying && (
              <span className="px-2 py-0.5 rounded text-[10px] font-semibold bg-violet-500/20 text-violet-300 border border-violet-500/30 animate-pulse">
                PLAYING
              </span>
            )}
          </div>

          {/* Métricas de Hardware */}
          <div className="mt-3 grid grid-cols-2 gap-2.5 text-xs">
            {/* CPU */}
            <div className="bg-zinc-950/40 p-2.5 rounded-lg border border-zinc-800/50">
              <div className="flex justify-between items-center text-zinc-400 mb-1">
                <span className="flex items-center gap-1 text-[11px]">
                  <Cpu className="w-3.5 h-3.5 text-zinc-400" /> CPU
                </span>
                <span className="font-mono font-semibold text-zinc-200">
                  {screen.cpuPercent ? `${screen.cpuPercent.toFixed(1)}%` : "0%"}
                </span>
              </div>
              <div className="w-full bg-zinc-800 rounded-full h-1.5 overflow-hidden">
                <div
                  className={`h-full transition-all duration-500 ${
                    screen.cpuPercent > 80
                      ? "bg-rose-500"
                      : screen.cpuPercent > 50
                      ? "bg-amber-500"
                      : "bg-indigo-500"
                  }`}
                  style={{ width: `${Math.min(100, Math.max(0, screen.cpuPercent || 0))}%` }}
                />
              </div>
            </div>

            {/* RAM */}
            <div className="bg-zinc-950/40 p-2.5 rounded-lg border border-zinc-800/50">
              <div className="flex justify-between items-center text-zinc-400 mb-1">
                <span className="flex items-center gap-1 text-[11px]">
                  <Cpu className="w-3.5 h-3.5 text-zinc-400" /> RAM
                </span>
                <span className="font-mono font-semibold text-zinc-200">
                  {screen.ramPercent ? `${screen.ramPercent.toFixed(1)}%` : "0%"}
                </span>
              </div>
              <div className="w-full bg-zinc-800 rounded-full h-1.5 overflow-hidden">
                <div
                  className="h-full bg-violet-500 transition-all duration-500"
                  style={{ width: `${Math.min(100, Math.max(0, screen.ramPercent || 0))}%` }}
                />
              </div>
            </div>

            {/* Disco */}
            <div className="bg-zinc-950/40 p-2 rounded-lg border border-zinc-800/50 flex items-center justify-between">
              <span className="flex items-center gap-1.5 text-zinc-400 text-[11px]">
                <HardDrive className="w-3.5 h-3.5 text-zinc-400" /> Disco
              </span>
              <span className="font-mono text-zinc-300 font-medium text-[11px]">
                {screen.freeDiskMb ? `${(screen.freeDiskMb / 1024).toFixed(1)} GB` : "N/D"}
              </span>
            </div>

            {/* Latido */}
            <div className="bg-zinc-950/40 p-2 rounded-lg border border-zinc-800/50 flex items-center justify-between">
              <span className="flex items-center gap-1.5 text-zinc-400 text-[11px]">
                <Clock className="w-3.5 h-3.5 text-zinc-400" /> Latido
              </span>
              <span className="text-zinc-300 font-medium text-[11px] truncate" title={screen.lastHeartbeat}>
                {formatTimeAgo(screen.lastHeartbeat)}
              </span>
            </div>
          </div>
        </div>

        {/* ─── 3. Controles de Reproducción ─────────────────────────────────── */}
        <div className="mt-4 pt-3 border-t border-zinc-800/60">
          <div className="grid grid-cols-3 gap-2">
            {/* Play */}
            <button
              onClick={handlePlay}
              disabled={!isOnline || !isAdmin || loadingAction === "play"}
              className="flex items-center justify-center gap-1.5 py-2 px-3 rounded-xl font-medium text-xs bg-emerald-600 hover:bg-emerald-500 text-white transition-all active:scale-95 disabled:opacity-40 disabled:pointer-events-none shadow-sm shadow-emerald-950"
              title={isAdmin ? "Reproducir medio con LibVLC" : "Solo administradores"}
            >
              <Play className={`w-3.5 h-3.5 fill-current ${loadingAction === "play" ? "animate-spin" : ""}`} />
              <span>Play</span>
            </button>

            {/* Pause */}
            <button
              onClick={handlePause}
              disabled={!isOnline || !isAdmin || loadingAction === "pause"}
              className="flex items-center justify-center gap-1.5 py-2 px-3 rounded-xl font-medium text-xs bg-amber-600/90 hover:bg-amber-500 text-white transition-all active:scale-95 disabled:opacity-40 disabled:pointer-events-none shadow-sm shadow-amber-950"
              title={isAdmin ? "Pausar o reanudar" : "Solo administradores"}
            >
              <Pause className={`w-3.5 h-3.5 fill-current ${loadingAction === "pause" ? "animate-spin" : ""}`} />
              <span>Pausar</span>
            </button>

            {/* Stop */}
            <button
              onClick={handleStop}
              disabled={!isOnline || !isAdmin || loadingAction === "stop"}
              className="flex items-center justify-center gap-1.5 py-2 px-3 rounded-xl font-medium text-xs bg-zinc-800 hover:bg-rose-600 text-zinc-200 hover:text-white transition-all active:scale-95 disabled:opacity-40 disabled:pointer-events-none border border-zinc-700/80 shadow-sm"
              title={isAdmin ? "Detener reproducción" : "Solo administradores"}
            >
              <Square className={`w-3.5 h-3.5 fill-current ${loadingAction === "stop" ? "animate-spin" : ""}`} />
              <span>Stop</span>
            </button>
          </div>

          {/* Encendido remoto (solo si la pantalla está apagada) */}
          {!isOnline && (
            <button
              onClick={handleWake}
              disabled={!canWake || waking || loadingAction === "wake"}
              className="mt-2 w-full flex items-center justify-center gap-2 py-3 px-3 rounded-xl font-semibold text-xs bg-gradient-to-r from-amber-600 to-orange-600 hover:from-amber-500 hover:to-orange-500 text-white transition-all active:scale-[0.98] disabled:opacity-40 disabled:pointer-events-none border border-amber-400/40 shadow-lg shadow-amber-950/60"
              title={wakeHint}
            >
              <Power className={`w-4 h-4 ${waking ? "animate-pulse" : ""}`} />
              <span>{waking ? `Despertando… ${wakeCountdown}s` : "Encender (Wake-on-LAN)"}</span>
            </button>
          )}

          {/* Explorador de archivos y acciones de sistema */}
          <div className="mt-2 grid grid-cols-2 gap-2">
            <button
              onClick={() => setShowFiles(true)}
              disabled={!isOnline || !isAdmin}
              className="w-full flex items-center justify-center gap-1.5 py-2 px-3 rounded-xl font-medium text-xs bg-indigo-600/90 hover:bg-indigo-500 text-white transition-all active:scale-95 disabled:opacity-40 disabled:pointer-events-none border border-indigo-500/50 shadow-sm shadow-indigo-950"
              title={isAdmin ? "Abrir explorador de archivos" : "Solo administradores"}
            >
              <FolderOpen className="w-3.5 h-3.5" />
              <span>Archivos</span>
            </button>

            <SystemActionsMenu
              screenId={screen.id}
              isOnline={isOnline}
              onSuccess={onSuccess}
              onError={onError}
            />
          </div>

          {/* Control total del escritorio (VNC) */}
          <button
            onClick={() => setShowRemote(true)}
            disabled={!isOnline || !isAdmin}
            className="mt-2 w-full flex items-center justify-center gap-2 py-2.5 px-3 rounded-xl font-semibold text-xs bg-gradient-to-r from-sky-600 via-indigo-600 to-violet-600 hover:from-sky-500 hover:via-indigo-500 hover:to-violet-500 text-white transition-all active:scale-[0.98] disabled:opacity-40 disabled:pointer-events-none border border-indigo-400/40 shadow-lg shadow-indigo-950/60"
            title={
              !isAdmin
                ? "Solo administradores"
                : screen.remoteControlAvailable === false
                ? "Abrir escritorio remoto (el agente aún no reporta TightVNC)"
                : "Abrir escritorio remoto (ratón y teclado)"
            }
          >
            <MonitorSmartphone className="w-4 h-4" />
            <span>Control Total</span>
          </button>

          {/* Menú desplegable para archivo o volumen */}
          <div className="mt-2.5">
            <button
              onClick={() => setShowOptions(!showOptions)}
              className="w-full flex items-center justify-center gap-1 text-[11px] text-zinc-400 hover:text-zinc-200 py-1 transition-colors"
            >
              <span>{showOptions ? "Ocultar opciones" : "Configurar medio / volumen"}</span>
              {showOptions ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
            </button>

            {showOptions && (
              <div className="mt-2 p-3 bg-zinc-950 rounded-xl border border-zinc-800 text-xs flex flex-col gap-2.5 animate-in fade-in duration-200">
                <div>
                  <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
                    Ruta del archivo o video (Local o URL)
                  </label>
                  <input
                    type="text"
                    value={customMedia}
                    onChange={(e) => setCustomMedia(e.target.value)}
                    placeholder="C:\videos\archivo.mp4"
                    className="w-full bg-zinc-900 border border-zinc-800 rounded-lg px-2.5 py-1.5 text-zinc-200 focus:outline-none focus:border-indigo-500 font-mono text-[11px]"
                  />
                </div>

                <div>
                  <div className="flex justify-between items-center mb-1">
                    <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold flex items-center gap-1">
                      <Volume2 className="w-3 h-3" /> Volumen ({customVolume}%)
                    </label>
                    <button
                      onClick={handleVolumeSubmit}
                      disabled={!isOnline || !isAdmin}
                      className="text-[10px] text-indigo-400 hover:text-indigo-300 font-medium disabled:opacity-40"
                    >
                      Aplicar volumen
                    </button>
                  </div>
                  <input
                    type="range"
                    min="0"
                    max="100"
                    value={customVolume}
                    onChange={(e) => setCustomVolume(Number(e.target.value))}
                    className="w-full accent-indigo-500 h-1 bg-zinc-800 rounded-lg appearance-none cursor-pointer"
                  />
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      <FileExplorerModal
        open={showFiles}
        screenId={screen.id}
        screenName={screen.name}
        onClose={() => setShowFiles(false)}
        onSuccess={onSuccess}
        onError={onError}
      />

      <RemoteDesktopModal
        open={showRemote}
        screenId={screen.id}
        screenName={screen.name}
        onClose={() => setShowRemote(false)}
        onSuccess={onSuccess}
        onError={onError}
      />
    </div>
  );
}
