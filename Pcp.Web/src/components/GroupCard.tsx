"use client";

import { useState } from "react";
import {
  Check,
  Film,
  Loader2,
  Pencil,
  Play,
  Power,
  RotateCcw,
  Trash2,
  Users,
  Volume2,
  X,
} from "lucide-react";
import { GroupInfo } from "@/types/group";
import { ScreenInfo } from "@/types/screen";
import { deleteGroup, sendGroupCommand, updateGroup } from "@/services/api";

interface GroupCardProps {
  group: GroupInfo;
  screens: ScreenInfo[];
  onChanged: () => Promise<void> | void;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

export function GroupCard({ group, screens, onChanged, onSuccess, onError }: GroupCardProps) {
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(group.name);
  const [selectedIds, setSelectedIds] = useState<string[]>(group.screenIds);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [showPlay, setShowPlay] = useState(false);
  const [mediaPath, setMediaPath] = useState("C:\\videos\\promo_verano.mp4");
  const [volume, setVolume] = useState(80);

  const assignedScreens = screens.filter((screen) => group.screenIds.includes(screen.id));
  const onlineCount = assignedScreens.filter(
    (screen) => screen.status.toLowerCase() === "online"
  ).length;

  const startEditing = () => {
    setName(group.name);
    setSelectedIds(group.screenIds);
    setEditing(true);
    setShowPlay(false);
  };

  const toggleScreen = (screenId: string) => {
    setSelectedIds((prev) =>
      prev.includes(screenId) ? prev.filter((id) => id !== screenId) : [...prev, screenId]
    );
  };

  const save = async () => {
    if (!name.trim()) {
      onError("Grupo", "El nombre del grupo es obligatorio.");
      return;
    }

    setSaving(true);

    try {
      await updateGroup(group.id, { name: name.trim(), screenIds: selectedIds });
      onSuccess("Grupo actualizado", `Se guardó "${name.trim()}" con ${selectedIds.length} pantalla(s).`);
      setEditing(false);
      await onChanged();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo actualizar el grupo";
      onError("Actualizar grupo", message);
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    if (!window.confirm(`¿Eliminar el grupo "${group.name}"? Las pantallas no se eliminarán.`)) {
      return;
    }

    setDeleting(true);

    try {
      await deleteGroup(group.id);
      onSuccess("Grupo eliminado", `Se eliminó "${group.name}".`);
      await onChanged();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo eliminar el grupo";
      onError("Eliminar grupo", message);
    } finally {
      setDeleting(false);
    }
  };

  const runMassAction = async (
    type: string,
    label: string,
    confirmMessage?: string,
    payload?: Record<string, unknown>
  ) => {
    if (group.screenIds.length === 0) {
      onError("Grupo vacío", "Asigna al menos una pantalla a este grupo.");
      return;
    }

    if (confirmMessage && !window.confirm(confirmMessage)) {
      return;
    }

    setBusyAction(type);
    setShowPlay(false);

    try {
      const result = await sendGroupCommand(group.id, { type, payload });
      const summary = `${label}: ${result.sent}/${result.total} enviados · ${result.offline} offline · ${result.failed} fallidos.`;

      if (result.sent > 0) {
        onSuccess("Comando masivo", summary);
      } else {
        onError("Comando masivo", summary);
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo enviar el comando al grupo";
      onError("Comando masivo", message);
    } finally {
      setBusyAction(null);
    }
  };

  const playInGroup = () =>
    runMassAction("play", "Reproducción", undefined, {
      mediaPath: mediaPath.trim() || "C:\\videos\\promo_verano.mp4",
      loop: true,
      volume,
      fitMode: "cover",
    });

  return (
    <div className="rounded-2xl border border-zinc-800 bg-zinc-900/80 shadow-lg overflow-hidden">
      <div className="p-5 border-b border-zinc-800/80">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            {editing ? (
              <input
                value={name}
                onChange={(event) => setName(event.target.value)}
                maxLength={100}
                className="w-full bg-zinc-950 border border-zinc-700 rounded-lg px-3 py-1.5 text-sm text-white focus:outline-none focus:border-indigo-500"
                placeholder="Nombre del grupo"
              />
            ) : (
              <h3 className="text-base font-semibold text-white truncate">{group.name}</h3>
            )}
            <p className="text-xs text-zinc-400 mt-1">
              {assignedScreens.length} pantalla(s) · {onlineCount} online
            </p>
          </div>

          <span className="w-9 h-9 rounded-xl bg-sky-500/15 border border-sky-500/30 flex items-center justify-center text-sky-400 flex-shrink-0">
            <Users className="w-5 h-5" />
          </span>
        </div>

        <div className="mt-3 flex flex-wrap gap-1.5">
          {assignedScreens.length === 0 ? (
            <span className="text-xs text-zinc-500">Sin pantallas asignadas.</span>
          ) : (
            assignedScreens.map((screen) => (
              <span
                key={screen.id}
                className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[10px] font-mono border ${
                  screen.status.toLowerCase() === "online"
                    ? "bg-emerald-500/10 border-emerald-500/25 text-emerald-300"
                    : "bg-zinc-800/60 border-zinc-700/60 text-zinc-400"
                }`}
                title={screen.name}
              >
                <span
                  className={`w-1.5 h-1.5 rounded-full ${
                    screen.status.toLowerCase() === "online" ? "bg-emerald-400" : "bg-zinc-500"
                  }`}
                />
                {screen.id}
              </span>
            ))
          )}
        </div>
      </div>

      {editing ? (
        <div className="p-5 space-y-3">
          <p className="text-[11px] uppercase tracking-wider text-zinc-500 font-semibold">
            Pantallas del grupo
          </p>
          <div className="max-h-52 overflow-auto space-y-1.5 pr-1">
            {screens.length === 0 ? (
              <p className="text-xs text-zinc-500">No hay pantallas registradas.</p>
            ) : (
              screens.map((screen) => {
                const selected = selectedIds.includes(screen.id);
                const otherGroup = screen.groupId && screen.groupId !== group.id;

                return (
                  <label
                    key={screen.id}
                    className={`flex items-center gap-2.5 px-2.5 py-2 rounded-lg border cursor-pointer transition-colors ${
                      selected
                        ? "bg-indigo-500/10 border-indigo-500/40"
                        : "bg-zinc-950/40 border-zinc-800 hover:border-zinc-700"
                    }`}
                  >
                    <input
                      type="checkbox"
                      checked={selected}
                      onChange={() => toggleScreen(screen.id)}
                      className="accent-indigo-500"
                    />
                    <span className="flex-1 min-w-0 text-xs text-zinc-200 truncate">
                      {screen.name || screen.id}
                      <span className="text-zinc-500 font-mono ml-1">({screen.id})</span>
                    </span>
                    {otherGroup && (
                      <span className="text-[10px] text-amber-400/80 truncate max-w-[35%]">
                        {screen.groupName}
                      </span>
                    )}
                  </label>
                );
              })
            )}
          </div>

          <div className="flex items-center gap-2 pt-1">
            <button
              type="button"
              onClick={() => void save()}
              disabled={saving}
              className="flex items-center gap-1.5 px-3 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-medium disabled:opacity-50"
            >
              {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
              Guardar
            </button>
            <button
              type="button"
              onClick={() => setEditing(false)}
              disabled={saving}
              className="flex items-center gap-1.5 px-3 py-2 rounded-lg bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium disabled:opacity-50"
            >
              <X className="w-3.5 h-3.5" />
              Cancelar
            </button>
          </div>
        </div>
      ) : (
        <div className="p-5 space-y-3">
          <div className="grid grid-cols-2 gap-2">
            <button
              type="button"
              onClick={() => setShowPlay((value) => !value)}
              disabled={busyAction !== null || group.screenIds.length === 0}
              className="flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg bg-violet-600/90 hover:bg-violet-500 text-white text-xs font-medium disabled:opacity-40"
            >
              <Play className="w-3.5 h-3.5 fill-current" />
              Reproducir
            </button>
            <button
              type="button"
              onClick={() =>
                void runMassAction(
                  "shutdown",
                  "Apagado masivo",
                  `¿Apagar las ${group.screenIds.length} pantalla(s) de "${group.name}"?`
                )
              }
              disabled={busyAction !== null || group.screenIds.length === 0}
              className="flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg bg-rose-600/90 hover:bg-rose-500 text-white text-xs font-medium disabled:opacity-40"
            >
              {busyAction === "shutdown" ? (
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
              ) : (
                <Power className="w-3.5 h-3.5" />
              )}
              Apagar todas
            </button>
            <button
              type="button"
              onClick={() =>
                void runMassAction(
                  "restart",
                  "Reinicio masivo",
                  `¿Reiniciar las ${group.screenIds.length} pantalla(s) de "${group.name}"?`
                )
              }
              disabled={busyAction !== null || group.screenIds.length === 0}
              className="flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg bg-amber-600/90 hover:bg-amber-500 text-white text-xs font-medium disabled:opacity-40"
            >
              {busyAction === "restart" ? (
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
              ) : (
                <RotateCcw className="w-3.5 h-3.5" />
              )}
              Reiniciar todas
            </button>
            <button
              type="button"
              onClick={startEditing}
              disabled={busyAction !== null}
              className="flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium border border-zinc-700/80 disabled:opacity-40"
            >
              <Pencil className="w-3.5 h-3.5" />
              Editar
            </button>
          </div>

          {showPlay && (
            <div className="p-3 rounded-xl bg-zinc-950 border border-zinc-800 space-y-2.5">
              <div>
                <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
                  Ruta o URL del video
                </label>
                <input
                  value={mediaPath}
                  onChange={(event) => setMediaPath(event.target.value)}
                  className="w-full bg-zinc-900 border border-zinc-800 rounded-lg px-2.5 py-1.5 text-zinc-200 text-[11px] font-mono focus:outline-none focus:border-indigo-500"
                  placeholder="C:\videos\archivo.mp4"
                />
              </div>

              <div>
                <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold flex items-center gap-1 mb-1">
                  <Volume2 className="w-3 h-3" /> Volumen ({volume}%)
                </label>
                <input
                  type="range"
                  min="0"
                  max="100"
                  value={volume}
                  onChange={(event) => setVolume(Number(event.target.value))}
                  className="w-full accent-indigo-500 h-1 bg-zinc-800 rounded-lg appearance-none cursor-pointer"
                />
              </div>

              <button
                type="button"
                onClick={() => void playInGroup()}
                disabled={busyAction !== null}
                className="w-full flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg bg-violet-600 hover:bg-violet-500 text-white text-xs font-medium disabled:opacity-50"
              >
                {busyAction === "play" ? (
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                ) : (
                  <Film className="w-3.5 h-3.5" />
                )}
                Reproducir en {group.screenIds.length} pantalla(s)
              </button>
            </div>
          )}

          <div className="flex items-center justify-between pt-1 border-t border-zinc-800/60">
            <button
              type="button"
              onClick={() => void remove()}
              disabled={deleting || busyAction !== null}
              className="flex items-center gap-1.5 text-[11px] text-zinc-500 hover:text-rose-400 transition-colors disabled:opacity-50"
            >
              {deleting ? (
                <Loader2 className="w-3 h-3 animate-spin" />
              ) : (
                <Trash2 className="w-3 h-3" />
              )}
              Eliminar grupo
            </button>
            <span className="text-[10px] text-zinc-600 font-mono">
              {group.id.slice(0, 8)}
            </span>
          </div>
        </div>
      )}
    </div>
  );
}
