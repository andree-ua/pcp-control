"use client";

import { useEffect, useMemo, useState } from "react";
import { CalendarPlus, Loader2, Save, X } from "lucide-react";
import { Playlist } from "@/types/playlist";
import { GroupInfo } from "@/types/group";
import { ScreenInfo } from "@/types/screen";
import { DAY_LABELS, Schedule, ScheduleUpsert, parseDays } from "@/types/schedule";

interface ScheduleFormProps {
  initial?: Schedule;
  playlists: Playlist[];
  screens: ScreenInfo[];
  groups: GroupInfo[];
  saving: boolean;
  onSubmit: (values: ScheduleUpsert) => Promise<void> | void;
  onCancel?: () => void;
}

const TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/;

/**
 * Formulario de horario: playlist, destino (pantalla o grupo), días ISO, hora
 * local del servidor y duración de la ventana.
 */
export function ScheduleForm({
  initial,
  playlists,
  screens,
  groups,
  saving,
  onSubmit,
  onCancel,
}: ScheduleFormProps) {
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

  const initialTarget = initial
    ? initial.groupId
      ? `group:${initial.groupId}`
      : initial.screenId
      ? `screen:${initial.screenId}`
      : ""
    : options[0]?.value ?? "";

  const [name, setName] = useState(initial?.name ?? "");
  const [playlistId, setPlaylistId] = useState(initial?.playlistId ?? playlists[0]?.id ?? "");
  const [target, setTarget] = useState(initialTarget);
  const [days, setDays] = useState<number[]>(() =>
    initial ? parseDays(initial.daysOfWeek) : [1, 2, 3, 4, 5]
  );
  const [startTime, setStartTime] = useState(initial?.startTime ?? "08:00");
  const [durationMinutes, setDurationMinutes] = useState(initial?.durationMinutes ?? 0);
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!options.some((option) => option.value === target)) {
      setTarget(options[0]?.value ?? "");
    }
  }, [options, target]);

  const toggleDay = (day: number) => {
    setDays((prev) =>
      prev.includes(day) ? prev.filter((value) => value !== day) : [...prev, day].sort((a, b) => a - b)
    );
  };

  const submit = async () => {
    if (!playlistId) {
      setError("Selecciona una playlist.");
      return;
    }

    if (!target) {
      setError("Selecciona una pantalla o un grupo de destino.");
      return;
    }

    if (days.length === 0) {
      setError("Selecciona al menos un día.");
      return;
    }

    if (!TIME_PATTERN.test(startTime)) {
      setError("La hora debe tener formato HH:mm.");
      return;
    }

    setError(null);

    const [kind, id] = target.split(":");

    await onSubmit({
      name: name.trim() || undefined,
      playlistId,
      screenId: kind === "screen" ? id : null,
      groupId: kind === "group" ? id : null,
      daysOfWeek: days,
      startTime,
      durationMinutes: Math.max(0, Math.round(durationMinutes || 0)),
      enabled,
    });
  };

  return (
    <div className="rounded-2xl border border-sky-800/60 bg-zinc-900/80 p-4 space-y-3">
      <div className="flex items-center justify-between gap-2">
        <p className="text-[11px] uppercase tracking-wider text-sky-300 font-semibold flex items-center gap-1.5">
          <CalendarPlus className="w-3.5 h-3.5" />
          {initial ? "Editar horario" : "Nuevo horario"}
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

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
        <div>
          <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
            Playlist
          </label>
          <select
            value={playlistId}
            onChange={(event) => setPlaylistId(event.target.value)}
            className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2 py-1.5 text-xs text-zinc-200 focus:outline-none focus:border-sky-500"
          >
            {playlists.length === 0 && <option value="">Sin playlists</option>}
            {playlists.map((playlist) => (
              <option key={playlist.id} value={playlist.id}>
                {playlist.name} ({playlist.items.length})
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
            Destino
          </label>
          <select
            value={target}
            onChange={(event) => setTarget(event.target.value)}
            className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2 py-1.5 text-xs text-zinc-200 focus:outline-none focus:border-sky-500"
          >
            {options.length === 0 && <option value="">Sin destinos</option>}
            {options.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
          Nombre (opcional)
        </label>
        <input
          value={name}
          onChange={(event) => setName(event.target.value)}
          maxLength={100}
          placeholder="Se genera con la playlist y la hora"
          className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2.5 py-1.5 text-xs text-zinc-200 focus:outline-none focus:border-sky-500"
        />
      </div>

      <div>
        <p className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold mb-1">
          Días de la semana
        </p>
        <div className="flex gap-1">
          {DAY_LABELS.map((label, index) => {
            const day = index + 1;
            const active = days.includes(day);

            return (
              <button
                key={day}
                type="button"
                onClick={() => toggleDay(day)}
                className={`w-8 h-8 rounded-lg text-[11px] font-semibold border transition-colors ${
                  active
                    ? "bg-sky-500/20 border-sky-500/50 text-sky-200"
                    : "bg-zinc-950 border-zinc-800 text-zinc-500 hover:text-zinc-300"
                }`}
                title={`Día ${day}`}
              >
                {label}
              </button>
            );
          })}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <div>
          <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
            Hora de inicio
          </label>
          <input
            type="time"
            value={startTime}
            onChange={(event) => setStartTime(event.target.value)}
            className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2 py-1.5 text-xs text-zinc-200 focus:outline-none focus:border-sky-500"
          />
        </div>

        <div>
          <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
            Ventana (min)
          </label>
          <input
            type="number"
            min={0}
            max={1440}
            value={durationMinutes}
            onChange={(event) => setDurationMinutes(Number(event.target.value))}
            className="w-full bg-zinc-950 border border-zinc-800 rounded-lg px-2 py-1.5 text-xs text-zinc-200 focus:outline-none focus:border-sky-500"
            title="0 = duración de la playlist (ventana mínima de 30 min para reintentos)"
          />
        </div>
      </div>

      <label className="flex items-center gap-2 text-xs text-zinc-300 cursor-pointer select-none">
        <input
          type="checkbox"
          checked={enabled}
          onChange={(event) => setEnabled(event.target.checked)}
          className="accent-sky-500"
        />
        Horario activo
      </label>

      {error && <p className="text-[11px] text-rose-400">{error}</p>}

      <button
        type="button"
        onClick={() => void submit()}
        disabled={saving}
        className="flex items-center gap-1.5 px-3 py-2 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-medium disabled:opacity-50"
      >
        {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Save className="w-3.5 h-3.5" />}
        {initial ? "Guardar cambios" : "Crear horario"}
      </button>
    </div>
  );
}
