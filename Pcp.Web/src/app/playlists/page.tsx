"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, CalendarClock, ListVideo, Loader2, Plus, RefreshCw } from "lucide-react";
import { useScreensSignalR } from "@/hooks/useScreensSignalR";
import { useAuth } from "@/context/AuthContext";
import { Header } from "@/components/Header";
import { ToastContainer } from "@/components/Toast";
import { PlaylistCard } from "@/components/PlaylistCard";
import { PlaylistEditor } from "@/components/PlaylistEditor";
import { ScheduleCard } from "@/components/ScheduleCard";
import { ScheduleForm } from "@/components/ScheduleForm";
import {
  createPlaylist,
  createSchedule,
  deletePlaylist,
  deleteSchedule,
  fetchGroups,
  fetchPlaylists,
  fetchSchedules,
  playPlaylist,
  runSchedule,
  updatePlaylist,
  updateSchedule,
} from "@/services/api";
import { GroupInfo } from "@/types/group";
import { Playlist, PlaylistTarget, PlaylistUpsert } from "@/types/playlist";
import { Schedule, ScheduleUpsert, parseDays } from "@/types/schedule";

export default function PlaylistsPage() {
  const { isAdmin } = useAuth();
  const {
    screens,
    loading: loadingScreens,
    connectionStatus,
    toasts,
    addToast,
    removeToast,
    refreshScreens,
  } = useScreensSignalR();

  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [schedules, setSchedules] = useState<Schedule[]>([]);
  const [groups, setGroups] = useState<GroupInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [creatingPlaylist, setCreatingPlaylist] = useState(false);
  const [editingPlaylist, setEditingPlaylist] = useState<Playlist | null>(null);
  const [savingPlaylist, setSavingPlaylist] = useState(false);

  const [creatingSchedule, setCreatingSchedule] = useState(false);
  const [editingSchedule, setEditingSchedule] = useState<Schedule | null>(null);
  const [savingSchedule, setSavingSchedule] = useState(false);

  const [busyId, setBusyId] = useState<string | null>(null);

  const refreshAll = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      const [playlistsData, schedulesData] = await Promise.all([fetchPlaylists(), fetchSchedules()]);
      setPlaylists(playlistsData);
      setSchedules(schedulesData);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudieron cargar las playlists";
      setError(message);
      addToast("Error de carga", message, "error");
    } finally {
      setLoading(false);
    }

    // Los grupos solo alimentan los selectores de destino: si fallan, la página sigue.
    try {
      setGroups(await fetchGroups());
    } catch {
      setGroups([]);
    }
  }, [addToast]);

  useEffect(() => {
    void refreshAll();
  }, [refreshAll]);

  const resetEditors = () => {
    setCreatingPlaylist(false);
    setEditingPlaylist(null);
    setCreatingSchedule(false);
    setEditingSchedule(null);
  };

  const handleSavePlaylist = async (values: PlaylistUpsert) => {
    setSavingPlaylist(true);

    try {
      if (editingPlaylist) {
        const updated = await updatePlaylist(editingPlaylist.id, values);
        addToast("Playlist actualizada", `${updated.name} · ${updated.items.length} elemento(s).`, "success");
      } else {
        const created = await createPlaylist(values);
        addToast("Playlist creada", `${created.name} · ${created.items.length} elemento(s).`, "success");
      }

      resetEditors();
      await refreshAll();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo guardar la playlist";
      addToast("Playlist", message, "error");
    } finally {
      setSavingPlaylist(false);
    }
  };

  const handleDeletePlaylist = async (playlist: Playlist) => {
    if (
      !window.confirm(
        `¿Eliminar la playlist "${playlist.name}"? También se eliminarán sus horarios asociados.`
      )
    ) {
      return;
    }

    setBusyId(playlist.id);

    try {
      await deletePlaylist(playlist.id);
      addToast("Playlist eliminada", playlist.name, "success");
      await refreshAll();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo eliminar la playlist";
      addToast("Eliminar playlist", message, "error");
    } finally {
      setBusyId(null);
    }
  };

  const handlePlay = async (playlist: Playlist, target: PlaylistTarget) => {
    setBusyId(playlist.id);

    try {
      const result = await playPlaylist(playlist.id, {
        groupId: target.groupId ?? null,
        screenIds: target.screenId ? [target.screenId] : undefined,
        startInSeconds: 8,
      });

      const when = new Date(result.startAtUtc).toLocaleTimeString();
      const extra = [
        result.offline > 0 ? `${result.offline} offline` : null,
        result.failed > 0 ? `${result.failed} fallidas` : null,
      ]
        .filter(Boolean)
        .join(" · ");
      const message = `${result.sent}/${result.total} pantalla(s) · arranque ${when}${extra ? ` · ${extra}` : ""}.`;

      if (result.sent > 0) {
        addToast("Playlist programada", message, "success");
      } else {
        addToast("Sin pantallas disponibles", message, "error");
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo reproducir la playlist";
      addToast("Reproducir playlist", message, "error");
    } finally {
      setBusyId(null);
    }
  };

  const handleSaveSchedule = async (values: ScheduleUpsert) => {
    setSavingSchedule(true);

    try {
      if (editingSchedule) {
        const updated = await updateSchedule(editingSchedule.id, values);
        addToast("Horario actualizado", `${updated.name} · ${updated.startTime}.`, "success");
      } else {
        const created = await createSchedule(values);
        addToast("Horario creado", `${created.name} · ${created.startTime}.`, "success");
      }

      resetEditors();
      await refreshAll();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo guardar el horario";
      addToast("Horario", message, "error");
    } finally {
      setSavingSchedule(false);
    }
  };

  const handleToggleSchedule = async (schedule: Schedule) => {
    setBusyId(schedule.id);

    try {
      await updateSchedule(schedule.id, {
        name: schedule.name,
        playlistId: schedule.playlistId,
        screenId: schedule.screenId ?? null,
        groupId: schedule.groupId ?? null,
        daysOfWeek: parseDays(schedule.daysOfWeek),
        startTime: schedule.startTime,
        durationMinutes: schedule.durationMinutes,
        enabled: !schedule.enabled,
      });

      addToast(
        schedule.enabled ? "Horario pausado" : "Horario activado",
        schedule.name,
        "success"
      );
      await refreshAll();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo cambiar el horario";
      addToast("Horario", message, "error");
    } finally {
      setBusyId(null);
    }
  };

  const handleRunSchedule = async (schedule: Schedule) => {
    setBusyId(schedule.id);

    try {
      const result = await runSchedule(schedule.id);
      const extra = result.offline > 0 ? ` · ${result.offline} offline` : "";
      const message = `${result.sent}/${result.total} pantalla(s)${extra}.`;

      if (result.sent > 0) {
        addToast("Horario ejecutado", `${schedule.name}: ${message}`, "success");
      } else {
        addToast("Sin pantallas disponibles", `${schedule.name}: ${message}`, "error");
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo ejecutar el horario";
      addToast("Ejecutar horario", message, "error");
    } finally {
      setBusyId(null);
    }
  };

  const handleDeleteSchedule = async (schedule: Schedule) => {
    if (!window.confirm(`¿Eliminar el horario "${schedule.name}"?`)) {
      return;
    }

    setBusyId(schedule.id);

    try {
      await deleteSchedule(schedule.id);
      addToast("Horario eliminado", schedule.name, "success");
      await refreshAll();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo eliminar el horario";
      addToast("Eliminar horario", message, "error");
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="min-h-screen flex flex-col bg-zinc-950 text-zinc-100">
      <Header
        connectionStatus={connectionStatus}
        onRefresh={() => {
          void refreshAll();
          void refreshScreens();
        }}
        isLoading={loading || loadingScreens}
      />

      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h2 className="text-xl font-bold text-white flex items-center gap-2">
              <ListVideo className="w-5 h-5 text-violet-400" />
              Playlists y programación
            </h2>
            <p className="text-sm text-zinc-400 mt-1">
              Secuencias de vídeos con duración propia y horarios que las lanzan automáticamente
              en una pantalla o en un grupo completo.
            </p>
          </div>

          <button
            type="button"
            onClick={() => void refreshAll()}
            disabled={loading}
            className="flex items-center gap-1.5 px-3 py-2 rounded-xl bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium border border-zinc-700/80 disabled:opacity-50"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? "animate-spin" : ""}`} />
            Recargar
          </button>
        </div>

        {error && (
          <div className="p-4 rounded-xl bg-rose-950/40 border border-rose-800/60 flex items-center gap-3 text-rose-300 text-sm">
            <AlertTriangle className="w-5 h-5 text-rose-400 flex-shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {/* ── Playlists ─────────────────────────────────────────────────── */}
        <section className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="text-sm font-semibold uppercase tracking-wider text-zinc-400">
              Playlists
            </h3>

            {isAdmin && !creatingPlaylist && !editingPlaylist && (
              <button
                type="button"
                onClick={() => {
                  setCreatingPlaylist(true);
                  setEditingPlaylist(null);
                  setCreatingSchedule(false);
                  setEditingSchedule(null);
                }}
                className="flex items-center gap-1.5 px-3 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-medium"
              >
                <Plus className="w-3.5 h-3.5" /> Nueva playlist
              </button>
            )}
          </div>

          {(creatingPlaylist || editingPlaylist) && (
            <PlaylistEditor
              key={editingPlaylist?.id ?? "new-playlist"}
              initial={editingPlaylist ?? undefined}
              saving={savingPlaylist}
              onSubmit={handleSavePlaylist}
              onCancel={() => {
                setCreatingPlaylist(false);
                setEditingPlaylist(null);
              }}
            />
          )}

          {loading && playlists.length === 0 ? (
            <div className="flex items-center justify-center gap-3 py-12 text-zinc-400">
              <Loader2 className="w-6 h-6 animate-spin text-indigo-500" />
              <span className="text-sm">Cargando playlists…</span>
            </div>
          ) : playlists.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 px-4 text-center rounded-2xl border border-zinc-800/80 bg-zinc-900/30">
              <div className="w-12 h-12 rounded-2xl bg-zinc-800/50 flex items-center justify-center text-zinc-500 mb-3 border border-zinc-700/40">
                <ListVideo className="w-6 h-6" />
              </div>
              <h4 className="text-base font-semibold text-white">Aún no hay playlists</h4>
              <p className="text-sm text-zinc-400 max-w-md mt-1">
                Crea una lista con varios vídeos y prográmala por días y horas.
              </p>
            </div>
          ) : (
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              {playlists.map((playlist) => (
                <PlaylistCard
                  key={playlist.id}
                  playlist={playlist}
                  screens={screens}
                  groups={groups}
                  busy={busyId === playlist.id}
                  readOnly={!isAdmin}
                  onPlay={handlePlay}
                  onEdit={(selected) => {
                    setEditingPlaylist(selected);
                    setCreatingPlaylist(false);
                    setCreatingSchedule(false);
                    setEditingSchedule(null);
                  }}
                  onDelete={handleDeletePlaylist}
                />
              ))}
            </div>
          )}
        </section>

        {/* ── Horarios ──────────────────────────────────────────────────── */}
        <section className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="text-sm font-semibold uppercase tracking-wider text-zinc-400 flex items-center gap-1.5">
              <CalendarClock className="w-4 h-4 text-sky-400" />
              Programación
            </h3>

            {isAdmin && !creatingSchedule && !editingSchedule && (
              <button
                type="button"
                onClick={() => {
                  setCreatingSchedule(true);
                  setEditingSchedule(null);
                  setCreatingPlaylist(false);
                  setEditingPlaylist(null);
                }}
                disabled={playlists.length === 0}
                className="flex items-center gap-1.5 px-3 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white text-xs font-medium disabled:opacity-40"
                title={playlists.length === 0 ? "Crea primero una playlist" : "Programar una playlist"}
              >
                <Plus className="w-3.5 h-3.5" /> Nuevo horario
              </button>
            )}
          </div>

          {(creatingSchedule || editingSchedule) && (
            <ScheduleForm
              key={editingSchedule?.id ?? "new-schedule"}
              initial={editingSchedule ?? undefined}
              playlists={playlists}
              screens={screens}
              groups={groups}
              saving={savingSchedule}
              onSubmit={handleSaveSchedule}
              onCancel={() => {
                setCreatingSchedule(false);
                setEditingSchedule(null);
              }}
            />
          )}

          {loading && schedules.length === 0 ? (
            <div className="flex items-center justify-center gap-3 py-12 text-zinc-400">
              <Loader2 className="w-6 h-6 animate-spin text-sky-500" />
              <span className="text-sm">Cargando horarios…</span>
            </div>
          ) : schedules.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 px-4 text-center rounded-2xl border border-zinc-800/80 bg-zinc-900/30">
              <div className="w-12 h-12 rounded-2xl bg-zinc-800/50 flex items-center justify-center text-zinc-500 mb-3 border border-zinc-700/40">
                <CalendarClock className="w-6 h-6" />
              </div>
              <h4 className="text-base font-semibold text-white">Sin horarios programados</h4>
              <p className="text-sm text-zinc-400 max-w-md mt-1">
                Los horarios se ejecutan solos en el servidor; la pantalla debe estar encendida.
              </p>
            </div>
          ) : (
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              {schedules.map((schedule) => (
                <ScheduleCard
                  key={schedule.id}
                  schedule={schedule}
                  busy={busyId === schedule.id}
                  readOnly={!isAdmin}
                  onToggle={handleToggleSchedule}
                  onRun={handleRunSchedule}
                  onEdit={(selected) => {
                    setEditingSchedule(selected);
                    setCreatingSchedule(false);
                    setCreatingPlaylist(false);
                    setEditingPlaylist(null);
                  }}
                  onDelete={handleDeleteSchedule}
                />
              ))}
            </div>
          )}
        </section>
      </main>

      <ToastContainer toasts={toasts} onDismiss={removeToast} />
    </div>
  );
}
