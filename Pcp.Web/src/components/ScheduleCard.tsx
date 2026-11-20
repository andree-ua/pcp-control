"use client";

import {
  CalendarClock,
  Loader2,
  Pencil,
  Play,
  Power,
  Trash2,
} from "lucide-react";
import { Schedule, describeDays } from "@/types/schedule";

interface ScheduleCardProps {
  schedule: Schedule;
  busy: boolean;
  readOnly?: boolean;
  onToggle: (schedule: Schedule) => Promise<void> | void;
  onRun: (schedule: Schedule) => Promise<void> | void;
  onEdit: (schedule: Schedule) => void;
  onDelete: (schedule: Schedule) => Promise<void> | void;
}

function formatMoment(value?: string | null): string {
  if (!value) return "—";

  try {
    return new Date(value).toLocaleString();
  } catch {
    return value;
  }
}

export function ScheduleCard({
  schedule,
  busy,
  readOnly = false,
  onToggle,
  onRun,
  onEdit,
  onDelete,
}: ScheduleCardProps) {
  const target = schedule.groupName
    ? `Grupo · ${schedule.groupName}`
    : schedule.screenName || schedule.screenId
    ? `Pantalla · ${schedule.screenName || schedule.screenId}`
    : "Sin destino";

  return (
    <div
      className={`rounded-2xl border bg-zinc-900/80 p-4 shadow-lg space-y-3 ${
        schedule.enabled ? "border-zinc-800" : "border-zinc-800/60 opacity-70"
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-white truncate flex items-center gap-2">
            <CalendarClock className="w-4 h-4 text-sky-400 flex-shrink-0" />
            {schedule.name}
          </h3>
          <p className="text-[11px] text-zinc-400 mt-1 truncate">
            {schedule.playlistName} · {target}
          </p>
        </div>

        <span
          className={`px-2 py-0.5 rounded-full text-[10px] font-semibold border flex-shrink-0 ${
            schedule.enabled
              ? "bg-emerald-500/10 border-emerald-500/30 text-emerald-300"
              : "bg-zinc-800/70 border-zinc-700 text-zinc-400"
          }`}
        >
          {schedule.enabled ? "ACTIVO" : "PAUSADO"}
        </span>
      </div>

      <div className="grid grid-cols-2 gap-2 text-[11px]">
        <div className="rounded-lg bg-zinc-950/60 border border-zinc-800/80 px-2.5 py-1.5">
          <p className="text-[9px] uppercase tracking-wider text-zinc-500 font-semibold">Cuándo</p>
          <p className="text-zinc-200 font-medium">
            {describeDays(schedule.daysOfWeek)} · {schedule.startTime}
          </p>
        </div>

        <div className="rounded-lg bg-zinc-950/60 border border-zinc-800/80 px-2.5 py-1.5">
          <p className="text-[9px] uppercase tracking-wider text-zinc-500 font-semibold">
            Próxima ejecución
          </p>
          <p className="text-zinc-200 font-medium truncate" title={formatMoment(schedule.nextRunAt)}>
            {schedule.enabled ? formatMoment(schedule.nextRunAt) : "—"}
          </p>
        </div>

        <div className="rounded-lg bg-zinc-950/60 border border-zinc-800/80 px-2.5 py-1.5">
          <p className="text-[9px] uppercase tracking-wider text-zinc-500 font-semibold">Ventana</p>
          <p className="text-zinc-200 font-medium">
            {schedule.durationMinutes > 0 ? `${schedule.durationMinutes} min` : "Duración de la playlist"}
          </p>
        </div>

        <div className="rounded-lg bg-zinc-950/60 border border-zinc-800/80 px-2.5 py-1.5">
          <p className="text-[9px] uppercase tracking-wider text-zinc-500 font-semibold">
            Última ejecución
          </p>
          <p className="text-zinc-200 font-medium truncate" title={formatMoment(schedule.lastTriggeredAt)}>
            {formatMoment(schedule.lastTriggeredAt)}
          </p>
        </div>
      </div>

      <div className="flex items-center justify-between pt-1 border-t border-zinc-800/60">
        <div className="flex flex-wrap items-center gap-3">
          {readOnly ? (
            <span className="text-[11px] text-zinc-500">Solo lectura</span>
          ) : (
            <>
          <button
            type="button"
            onClick={() => void onToggle(schedule)}
            disabled={busy}
            className="flex items-center gap-1 text-[11px] text-zinc-400 hover:text-emerald-300 disabled:opacity-40"
            title={schedule.enabled ? "Pausar horario" : "Activar horario"}
          >
            {busy ? (
              <Loader2 className="w-3 h-3 animate-spin" />
            ) : (
              <Power className="w-3 h-3" />
            )}
            {schedule.enabled ? "Pausar" : "Activar"}
          </button>

          <button
            type="button"
            onClick={() => void onRun(schedule)}
            disabled={busy}
            className="flex items-center gap-1 text-[11px] text-zinc-400 hover:text-sky-300 disabled:opacity-40"
            title="Ejecutar ahora"
          >
            <Play className="w-3 h-3 fill-current" /> Ejecutar
          </button>

          <button
            type="button"
            onClick={() => onEdit(schedule)}
            disabled={busy}
            className="flex items-center gap-1 text-[11px] text-zinc-400 hover:text-zinc-200 disabled:opacity-40"
          >
            <Pencil className="w-3 h-3" /> Editar
          </button>

          <button
            type="button"
            onClick={() => void onDelete(schedule)}
            disabled={busy}
            className="flex items-center gap-1 text-[11px] text-zinc-500 hover:text-rose-400 disabled:opacity-40"
          >
            <Trash2 className="w-3 h-3" /> Eliminar
          </button>
            </>
          )}
        </div>

        <span className="text-[10px] text-zinc-600 font-mono truncate" title={schedule.timeZone ?? undefined}>
          {schedule.timeZone ?? "UTC"}
        </span>
      </div>
    </div>
  );
}
