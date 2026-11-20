"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, Loader2, Plus, Users } from "lucide-react";
import { useScreensSignalR } from "@/hooks/useScreensSignalR";
import { Header } from "@/components/Header";
import { GroupCard } from "@/components/GroupCard";
import { ToastContainer } from "@/components/Toast";
import { createGroup, fetchGroups } from "@/services/api";
import { GroupInfo } from "@/types/group";
import { useAuth } from "@/context/AuthContext";

export default function GroupsPage() {
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

  const [groups, setGroups] = useState<GroupInfo[]>([]);
  const [loadingGroups, setLoadingGroups] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [creating, setCreating] = useState(false);

  const loadGroups = useCallback(async () => {
    setLoadingGroups(true);
    setError(null);

    try {
      const data = await fetchGroups();
      setGroups(data);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudieron cargar los grupos";
      setError(message);
      addToast("Error al cargar grupos", message, "error");
    } finally {
      setLoadingGroups(false);
    }
  }, [addToast]);

  useEffect(() => {
    void loadGroups();
  }, [loadGroups]);

  const toggleScreen = (screenId: string) => {
    setSelectedIds((prev) =>
      prev.includes(screenId) ? prev.filter((id) => id !== screenId) : [...prev, screenId]
    );
  };

  const handleCreate = async () => {
    const trimmed = name.trim();

    if (!trimmed) {
      addToast("Grupo", "El nombre del grupo es obligatorio.", "error");
      return;
    }

    setCreating(true);

    try {
      await createGroup({ name: trimmed, screenIds: selectedIds });
      addToast(
        "Grupo creado",
        `"${trimmed}" con ${selectedIds.length} pantalla(s).`,
        "success"
      );
      setName("");
      setSelectedIds([]);
      await loadGroups();
      await refreshScreens();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo crear el grupo";
      addToast("Error al crear grupo", message, "error");
    } finally {
      setCreating(false);
    }
  };

  const handleChanged = async () => {
    await loadGroups();
    await refreshScreens();
  };

  const onSuccess = (title: string, message: string) => addToast(title, message, "success");
  const onError = (title: string, message: string) => addToast(title, message, "error");

  return (
    <div className="min-h-screen flex flex-col bg-zinc-950 text-zinc-100">
      <Header
        connectionStatus={connectionStatus}
        onRefresh={() => {
          void loadGroups();
          void refreshScreens();
        }}
        isLoading={loadingGroups || loadingScreens}
      />

      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        <div>
          <h2 className="text-xl font-bold text-white flex items-center gap-2">
            <Users className="w-5 h-5 text-sky-400" />
            Grupos y Control Masivo
          </h2>
          <p className="text-sm text-zinc-400 mt-1">
            Agrupa pantallas y envía comandos de reproducción o de sistema a todas a la vez.
          </p>
        </div>

        {error && (
          <div className="p-4 rounded-xl bg-rose-950/40 border border-rose-800/60 flex items-center gap-3 text-rose-300 text-sm">
            <AlertTriangle className="w-5 h-5 text-rose-400 flex-shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {/* Crear grupo */}
        {isAdmin && (
        <section className="rounded-2xl border border-zinc-800 bg-zinc-900/60 p-5 space-y-4">
          <h3 className="text-sm font-semibold uppercase tracking-wider text-zinc-400">
            Nuevo grupo
          </h3>

          <div className="flex flex-col md:flex-row gap-3 md:items-end">
            <div className="flex-1">
              <label className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold block mb-1">
                Nombre del grupo
              </label>
              <input
                value={name}
                onChange={(event) => setName(event.target.value)}
                maxLength={100}
                placeholder='Ej. "Planta Baja" o "Recepción"'
                className="w-full bg-zinc-950 border border-zinc-800 rounded-xl px-3 py-2 text-sm text-white focus:outline-none focus:border-indigo-500"
              />
            </div>

            <button
              type="button"
              onClick={() => void handleCreate()}
              disabled={creating}
              className="flex items-center justify-center gap-1.5 px-4 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-medium disabled:opacity-50"
            >
              {creating ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Plus className="w-4 h-4" />
              )}
              Crear grupo
            </button>
          </div>

          <div>
            <p className="text-[10px] uppercase tracking-wider text-zinc-400 font-semibold mb-2">
              Pantallas iniciales ({selectedIds.length} seleccionadas)
            </p>

            {screens.length === 0 ? (
              <p className="text-xs text-zinc-500">No hay pantallas registradas todavía.</p>
            ) : (
              <div className="max-h-44 overflow-auto grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-1.5 pr-1">
                {screens.map((screen) => {
                  const selected = selectedIds.includes(screen.id);

                  return (
                    <label
                      key={screen.id}
                      className={`flex items-center gap-2 px-2.5 py-2 rounded-lg border cursor-pointer transition-colors ${
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
                      {screen.groupName && screen.groupId && (
                        <span className="text-[10px] text-amber-400/80 truncate max-w-[35%]">
                          {screen.groupName}
                        </span>
                      )}
                    </label>
                  );
                })}
              </div>
            )}
          </div>
        </section>
        )}

        {/* Listado de grupos */}
        {loadingGroups && groups.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-20 text-zinc-400 gap-3">
            <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            <p className="text-sm font-medium">Cargando grupos...</p>
          </div>
        ) : groups.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 px-4 text-center rounded-2xl border border-zinc-800/80 bg-zinc-900/30">
            <div className="w-14 h-14 rounded-2xl bg-zinc-800/50 flex items-center justify-center text-zinc-500 mb-4 border border-zinc-700/40">
              <Users className="w-7 h-7" />
            </div>
            <h3 className="text-lg font-semibold text-white">Aún no hay grupos</h3>
            <p className="text-sm text-zinc-400 max-w-md mt-1">
              Crea un grupo arriba, asígnale pantallas y aparecerán aquí las acciones masivas.
            </p>
          </div>
        ) : (
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {groups.map((group) => (
              <GroupCard
                key={group.id}
                group={group}
                screens={screens}
                onChanged={handleChanged}
                onSuccess={onSuccess}
                onError={onError}
              />
            ))}
          </div>
        )}
      </main>

      <ToastContainer toasts={toasts} onDismiss={removeToast} />
    </div>
  );
}
