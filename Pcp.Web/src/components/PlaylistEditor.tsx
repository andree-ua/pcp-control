"use client";

import { useState } from "react";
import { ArrowDown, ArrowUp, Loader2, Plus, Save, Trash2, X } from "lucide-react";
import { Playlist, PlaylistItemInput, PlaylistUpsert } from "@/types/playlist";

interface PlaylistEditorProps {
  initial?: Playlist;
  saving: boolean;
  onSubmit: (values: PlaylistUpsert) => Promise<void> | void;
  onCancel?: () => void;
}

const DEFAULT_MEDIA = "C:\\videos\\promo_verano.mp4";

function toInputs(playlist?: Playlist): PlaylistItemInput[] {
  if (!playlist || playlist.items.length === 0) {
    return [{ mediaPath: DEFAULT_MEDIA, durationSeconds: 0, volume: 80 }];
  }

  return playlist.items.map((item) => ({
    mediaPath: item.mediaPath,
    durationSeconds: item.durationSeconds,
    volume: item.volume,
  }));
}

/**
 * Editor de playlist: nombre, bucle y elementos ordenados. La duración 0
 * significa "hasta el final natural del vídeo".
 */
export function PlaylistEditor({ initial, saving, onSubmit, onCancel }: PlaylistEditorProps) {
  const [name, setName] = useState(initial?.name ?? "");
  const [loop, setLoop] = useState(initial?.loop ?? true);
  const [items, setItems] = useState<PlaylistItemInput[]>(() => toInputs(initial));
  const [error, setError] = useState<string | null>(null);

  const patchItem = (index: number, patch: Partial<PlaylistItemInput>) => {
    setItems((prev) =>
      prev.map((item, position) => (position === index ? { ...item, ...patch } : item))
    );
  };

  const moveItem = (index: number, direction: -1 | 1) => {
    setItems((prev) => {
      const target = index + direction;
      if (target < 0 || target >= prev.length) return prev;

      const next = [...prev];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  };

  const removeItem = (index: number) => {
    setItems((prev) => prev.filter((_, position) => position !== index));
  };

  const addItem = () => {
    setItems((prev) => [...prev, { mediaPath: "", durationSeconds: 0, volume: 80 }]);
  };

  const submit = async () => {
    const trimmed = name.trim();

    if (!trimmed) {
      setError("El nombre de la playlist es obligatorio.");
      return;
    }

    if (items.length === 0) {
      setError("Añade al menos un elemento a la playlist.");
      return;
    }

    if (items.some((item) => !item.mediaPath.trim())) {
      setError("Todos los elementos necesitan una ruta de medio.");
      return;
    }

    setError(null);

    await onSubmit({
      name: trimmed,
      loop,
      items: items.map((item) => ({
        mediaPath: item.mediaPath.trim(),
        durationSeconds: Math.max(0, Math.round(item.durationSeconds || 0)),
        volume: Math.min(100, Math.max(0, Math.round(item.volume || 0))),
      })),
    });
  };

  return (
    <div className="rounded-2xl border border-indigo-800/60 bg-zinc-900/80 p-4 space-y-3">
      <div className="flex items-center justify-between gap-2">
        <p className="text-[11px] uppercase tracking-wider text-indigo-300 font-semibold">
          {initial ? "Editar playlist" : "Nueva playlist"}
        </p>
        {onCancel && (
          <button
            type="button"
            onClick={onCancel}
            className="text-[11px] text-zinc-500 hover:text-zinc-300 flex items-center gap-1"
          >
            <X className="w-3 h-3" /> Cerrar
          </button>
        )}
      </div>

      <div>
        <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
          Nombre
        </label>
        <input
          value={name}
          onChange={(event) => setName(event.target.value)}
          maxLength={100}
          placeholder='Ej. "Promociones mañana"'
          className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2.5 py-1.5 text-sm text-zinc-200 focus:outline-none focus:border-indigo-500"
        />
      </div>

      <label className="flex items-center gap-2 text-xs text-zinc-300 cursor-pointer select-none">
        <input
          type="checkbox"
          checked={loop}
          onChange={(event) => setLoop(event.target.checked)}
          className="accent-indigo-500"
        />
        Repetir en bucle al terminar el último elemento
      </label>

      <div className="space-y-1.5">
        <p className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold">
          Elementos ({items.length})
        </p>

        {items.map((item, index) => (
          <div
            key={index}
            className="rounded-lg border border-zinc-800 bg-zinc-950/60 p-2 space-y-1.5"
          >
            <div className="flex items-center gap-1.5">
              <span className="text-[10px] font-mono text-zinc-500 w-5 text-center">
                {index + 1}
              </span>
              <input
                value={item.mediaPath}
                onChange={(event) => patchItem(index, { mediaPath: event.target.value })}
                placeholder="C:\videos\archivo.mp4 o https://…"
                className="flex-1 min-w-0 bg-zinc-900 border border-zinc-800 rounded-lg px-2 py-1 text-[11px] font-mono text-zinc-200 focus:outline-none focus:border-indigo-500"
              />
              <button
                type="button"
                onClick={() => moveItem(index, -1)}
                disabled={index === 0}
                className="p-1 rounded text-zinc-400 hover:text-white hover:bg-zinc-800 disabled:opacity-30"
                title="Subir"
              >
                <ArrowUp className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                onClick={() => moveItem(index, 1)}
                disabled={index === items.length - 1}
                className="p-1 rounded text-zinc-400 hover:text-white hover:bg-zinc-800 disabled:opacity-30"
                title="Bajar"
              >
                <ArrowDown className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                onClick={() => removeItem(index)}
                className="p-1 rounded text-zinc-500 hover:text-rose-400 hover:bg-zinc-800"
                title="Quitar elemento"
              >
                <Trash2 className="w-3.5 h-3.5" />
              </button>
            </div>

            <div className="flex items-center gap-3 pl-6 text-[10px] text-zinc-500">
              <label className="flex items-center gap-1">
                Duración
                <input
                  type="number"
                  min={0}
                  max={86400}
                  value={item.durationSeconds}
                  onChange={(event) =>
                    patchItem(index, { durationSeconds: Number(event.target.value) })
                  }
                  className="w-16 bg-zinc-900 border border-zinc-800 rounded px-1.5 py-0.5 text-zinc-200 text-[11px]"
                  title="0 = hasta el final del vídeo"
                />
                s <span className="text-zinc-600">(0 = fin del vídeo)</span>
              </label>

              <label className="flex items-center gap-1">
                Volumen
                <input
                  type="number"
                  min={0}
                  max={100}
                  value={item.volume}
                  onChange={(event) => patchItem(index, { volume: Number(event.target.value) })}
                  className="w-14 bg-zinc-900 border border-zinc-800 rounded px-1.5 py-0.5 text-zinc-200 text-[11px]"
                />
                % <span className="text-zinc-600">(0 = por defecto)</span>
              </label>
            </div>
          </div>
        ))}

        <button
          type="button"
          onClick={addItem}
          className="w-full flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg border border-dashed border-zinc-700 text-zinc-400 hover:text-zinc-200 hover:border-zinc-500 text-[11px]"
        >
          <Plus className="w-3.5 h-3.5" /> Añadir elemento
        </button>
      </div>

      {error && <p className="text-[11px] text-rose-400">{error}</p>}

      <div className="flex items-center gap-2 pt-1">
        <button
          type="button"
          onClick={() => void submit()}
          disabled={saving}
          className="flex items-center gap-1.5 px-3 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-medium disabled:opacity-50"
        >
          {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Save className="w-3.5 h-3.5" />}
          {initial ? "Guardar cambios" : "Crear playlist"}
        </button>
      </div>
    </div>
  );
}
