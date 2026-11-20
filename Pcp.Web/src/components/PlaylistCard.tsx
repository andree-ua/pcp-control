"use client";

import { useEffect, useMemo, useState } from "react";
import { Film, Loader2, Pencil, Play, Repeat, Trash2 } from "lucide-react";
import { Playlist, PlaylistTarget } from "@/types/playlist";
import { GroupInfo } from "@/types/group";
import { ScreenInfo } from "@/types/screen";

interface PlaylistCardProps {
  playlist: Playlist;
  screens: ScreenInfo[];
  groups: GroupInfo[];
  busy: boolean;
  readOnly?: boolean;
  onPlay: (playlist: Playlist, target: PlaylistTarget) => Promise<void> | void;
  onEdit: (playlist: Playlist) => void;
  onDelete: (playlist: Playlist) => Promise<void> | void;
}

function formatDuration(seconds: number): string {
  if (seconds <= 0) return "fin del vídeo";
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return rest === 0 ? `${minutes} min` : `${minutes}:${String(rest).padStart(2, "0")}`;
}

export function PlaylistCard({
  playlist,
  screens,
  groups,
  busy,
  readOnly = false,
  onPlay,
  onEdit,
  onDelete,
}: PlaylistCardProps) {
  const options = useMemo(
    () => [
      ...groups.map((group) => ({ value: `group:${group.id}`, label: `Grupo · ${group.name}` })),
      ...screens.map((screen) => ({
        value: `screen:${screen.id}`,
        label: `Pantalla · ${screen.name || screen.id}`,
      })),
    ],
    [groups, screens]
  );

  const [target, setTarget] = useState(options[0]?.value ?? "");

  useEffect(() => {
    if (!options.some((option) => option.value === target)) {
      setTarget(options[0]?.value ?? "");
    }
  }, [options, target]);

  const playNow = () => {
    if (!target) return;
    const [kind, id] = target.split(":");

    void onPlay(playlist, kind === "group" ? { groupId: id } : { screenId: id });
  };

  const totalPlanned = playlist.items.reduce((sum, item) => sum + item.durationSeconds, 0);

  return (
    <div className="rounded-2xl border border-zinc-800 bg-zinc-900/80 shadow-lg overflow-hidden flex flex-col">
      <div className="p-4 border-b border-zinc-800/80 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-white truncate flex items-center gap-2">
            <Film className="w-4 h-4 text-violet-400 flex-shrink-0" />
            {playlist.name}
          </h3>
          <p className="text-[11px] text-zinc-400 mt-1">
            {playlist.items.length} elemento(s)
            {totalPlanned > 0 && ` · ${formatDuration(totalPlanned)} planificados`}
          </p>
        </div>

        {playlist.loop && (
          <span className="flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-violet-500/15 border border-violet-500/30 text-violet-300">
            <Repeat className="w-3 h-3" /> LOOP
          </span>
        )}
      </div>

      <div className="p-4 flex-1 space-y-1">
        {playlist.items.length === 0 ? (
          <p className="text-[11px] text-zinc-500">Sin elementos. Edita la playlist para añadir medios.</p>
        ) : (
          playlist.items.map((item, index) => (
            <div key={item.id} className="flex items-center gap-2 text-[11px]">
              <span className="font-mono text-zinc-600 w-4 text-right">{index + 1}</span>
              <span className="flex-1 min-w-0 truncate font-mono text-zinc-300" title={item.mediaPath}>
                {item.mediaPath}
              </span>
              <span className="text-zinc-500 flex-shrink-0">
                {formatDuration(item.durationSeconds)}
                {item.volume > 0 && ` · ${item.volume}%`}
              </span>
            </div>
          ))
        )}
      </div>

      <div className="p-4 pt-0 space-y-2">
        {readOnly ? (
          <p className="text-[11px] text-zinc-500 pt-2 border-t border-zinc-800/60">
            Solo lectura. Un administrador puede reproducir o editar la playlist.
          </p>
        ) : (
          <>
            <div className="flex items-center gap-1.5">
          <select
            value={target}
            onChange={(event) => setTarget(event.target.value)}
            disabled={busy || options.length === 0}
            className="flex-1 min-w-0 bg-zinc-950 border border-zinc-800 rounded-lg px-2 py-1.5 text-[11px] text-zinc-200 focus:outline-none focus:border-indigo-500 disabled:opacity-50"
            title="Destino de la reproducción"
          >
            {options.length === 0 && <option value="">Sin destinos</option>}
            {options.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>

          <button
            type="button"
            onClick={playNow}
            disabled={busy || !target}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-violet-600 hover:bg-violet-500 text-white text-[11px] font-medium disabled:opacity-40"
            title="Reproducir ahora en el destino seleccionado"
          >
            {busy ? (
              <Loader2 className="w-3.5 h-3.5 animate-spin" />
            ) : (
              <Play className="w-3.5 h-3.5 fill-current" />
            )}
            Reproducir
          </button>
        </div>

        <div className="flex items-center justify-between pt-1 border-t border-zinc-800/60">
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => onEdit(playlist)}
              disabled={busy}
              className="flex items-center gap-1 text-[11px] text-zinc-400 hover:text-zinc-200 disabled:opacity-40"
            >
              <Pencil className="w-3 h-3" /> Editar
            </button>
            <button
              type="button"
              onClick={() => void onDelete(playlist)}
              disabled={busy}
              className="flex items-center gap-1 text-[11px] text-zinc-500 hover:text-rose-400 disabled:opacity-40"
            >
              <Trash2 className="w-3 h-3" /> Eliminar
            </button>
          </div>
          <span className="text-[10px] text-zinc-600 font-mono">
            {playlist.id.slice(0, 8)}
          </span>
        </div>
          </>
        )}
      </div>
    </div>
  );
}
