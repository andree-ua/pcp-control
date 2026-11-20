"use client";

import { useScreensSignalR } from "@/hooks/useScreensSignalR";
import { Header } from "@/components/Header";
import { SummaryBar } from "@/components/SummaryBar";
import { ScreenCard } from "@/components/ScreenCard";
import { ToastContainer } from "@/components/Toast";
import { MonitorX, Loader2, AlertTriangle, PlusCircle } from "lucide-react";

export default function DashboardPage() {
  const {
    screens,
    loading,
    error,
    connectionStatus,
    toasts,
    addToast,
    removeToast,
    refreshScreens,
  } = useScreensSignalR();

  return (
    <div className="min-h-screen flex flex-col bg-zinc-950 text-zinc-100">
      {/* Encabezado Principal */}
      <Header
        connectionStatus={connectionStatus}
        onRefresh={refreshScreens}
        isLoading={loading}
      />

      {/* Contenido Principal */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {/* Barra de Estadísticas Globales */}
        <SummaryBar screens={screens} />

        {/* Advertencia de conexión al servidor si hay error de fetch */}
        {error && (
          <div className="p-4 rounded-xl bg-rose-950/40 border border-rose-800/60 flex items-center justify-between text-rose-300 text-sm">
            <div className="flex items-center gap-3">
              <AlertTriangle className="w-5 h-5 text-rose-400 flex-shrink-0" />
              <span>
                <strong>No se pudo conectar con el servidor:</strong> {error}. Asegúrate de que Pcp.Server esté corriendo en{" "}
                <code className="bg-rose-900/40 px-1.5 py-0.5 rounded text-xs font-mono">http://localhost:5000</code>.
              </span>
            </div>
            <button
              onClick={refreshScreens}
              className="px-3 py-1 bg-rose-800/60 hover:bg-rose-700 text-white rounded-lg text-xs font-medium transition-colors"
            >
              Reintentar
            </button>
          </div>
        )}

        {/* Estado de Carga */}
        {loading && screens.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-24 text-zinc-400 gap-3">
            <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            <p className="text-sm font-medium">Buscando pantallas registradas...</p>
          </div>
        ) : screens.length === 0 ? (
          /* Estado Vacío: Sin pantallas registradas */
          <div className="flex flex-col items-center justify-center py-20 px-4 text-center rounded-2xl border border-zinc-800/80 bg-zinc-900/30">
            <div className="w-16 h-16 rounded-2xl bg-zinc-800/50 flex items-center justify-center text-zinc-500 mb-4 border border-zinc-700/40">
              <MonitorX className="w-8 h-8" />
            </div>
            <h3 className="text-lg font-semibold text-white">No hay pantallas registradas</h3>
            <p className="text-sm text-zinc-400 max-w-md mt-1 mb-6">
              Inicia uno o varios agentes <code className="text-zinc-200 bg-zinc-800 px-1 py-0.5 rounded">Pcp.Agent</code> en una terminal para que se vinculen automáticamente a este panel.
            </p>
            <div className="bg-zinc-950 p-4 rounded-xl border border-zinc-800/80 font-mono text-xs text-zinc-300 text-left">
              <p className="text-zinc-400 mb-1"># Para levantar un agente en PowerShell:</p>
              <p className="text-emerald-400">cd C:\Proyectos\Pcp\Pcp.Agent</p>
              <p className="text-emerald-400">dotnet run --Agent:ScreenId=screen-01</p>
            </div>
          </div>
        ) : (
          /* Grid de Tarjetas de Pantallas */
          <div>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-sm font-semibold uppercase tracking-wider text-zinc-400">
                Dispositivos Conectados ({screens.length})
              </h2>
              <span className="text-xs text-zinc-400 flex items-center gap-1.5">
                <span className="w-2 h-2 rounded-full bg-indigo-500"></span> Actualización reactiva por SignalR
              </span>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
              {screens.map((screen) => (
                <ScreenCard
                  key={screen.id}
                  screen={screen}
                  onSuccess={(title, msg) => addToast(title, msg, "success")}
                  onError={(title, msg) => addToast(title, msg, "error")}
                />
              ))}
            </div>
          </div>
        )}
      </main>

      {/* Contenedor de Toasts / Alertas flotantes */}
      <ToastContainer toasts={toasts} onDismiss={removeToast} />
    </div>
  );
}
