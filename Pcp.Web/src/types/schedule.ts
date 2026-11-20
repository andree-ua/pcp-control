export interface Schedule {
  id: string;
  name: string;
  playlistId: string;
  playlistName: string;
  screenId?: string | null;
  screenName?: string | null;
  groupId?: string | null;
  groupName?: string | null;
  /** Días ISO separados por coma: 1 = lunes … 7 = domingo. */
  daysOfWeek: string;
  /** Hora local "HH:mm" en la zona del servidor. */
  startTime: string;
  /** 0 = duración de la playlist (mínimo 30 min de ventana). */
  durationMinutes: number;
  enabled: boolean;
  lastTriggeredAt?: string | null;
  timeZone?: string | null;
  nextRunAt?: string | null;
  createdAt: string;
}

export interface ScheduleUpsert {
  name?: string;
  playlistId: string;
  screenId?: string | null;
  groupId?: string | null;
  daysOfWeek: number[];
  startTime: string;
  durationMinutes: number;
  enabled: boolean;
}

/** Etiquetas de los días ISO (índice 0 = lunes). */
export const DAY_LABELS = ["L", "M", "X", "J", "V", "S", "D"];

/** "1,2,3,4,5" → [1,2,3,4,5] */
export function parseDays(daysOfWeek: string): number[] {
  return daysOfWeek
    .split(",")
    .map((value) => Number(value.trim()))
    .filter((value) => Number.isInteger(value) && value >= 1 && value <= 7);
}

/** Texto corto y legible de los días de un horario. */
export function describeDays(daysOfWeek: string): string {
  const days = parseDays(daysOfWeek);

  if (days.length === 7) return "Todos los días";
  if (days.length === 0) return "Sin días";

  const sorted = [...days].sort((a, b) => a - b);
  const isWeekdays = sorted.length === 5 && sorted.every((day, index) => day === index + 1);

  if (isWeekdays) return "L–V";
  if (sorted.length === 2 && sorted[0] === 6 && sorted[1] === 7) return "Fin de semana";

  return sorted.map((day) => DAY_LABELS[day - 1] ?? "?").join(" ");
}
