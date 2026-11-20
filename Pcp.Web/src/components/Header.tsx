"use client";

import { RefreshCw, Monitor, Radio, Users, LogOut, ShieldCheck, Eye } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAuth } from "@/context/AuthContext";

interface HeaderProps {
  connectionStatus: "connected" | "reconnecting" | "disconnected";
  onRefresh: () => void;
  isLoading: boolean;
}

export function Header({ connectionStatus, onRefresh, isLoading }: HeaderProps) {
  const pathname = usePathname();
  const { user, isAdmin, signOut } = useAuth();

  return (
    <header className="border-b border-zinc-800 bg-zinc-900/60 backdrop-blur-md sticky top-0 z-40">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 h-16 flex items-center justify-between">
        {/* Brand / Logo */}
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-tr from-indigo-600 to-violet-500 flex items-center justify-center shadow-lg shadow-indigo-500/20">
            <Monitor className="w-5 h-5 text-white" />
          </div>
          <div>
            <h1 className="text-lg font-bold text-white tracking-tight flex items-center gap-2">
              PCP
              <span className="text-xs px-2 py-0.5 rounded-full bg-zinc-800 text-zinc-300 font-medium border border-zinc-700">
                Fase 3
              </span>
            </h1>
            <p className="text-xs text-zinc-400">Panel de Control de Pantallas</p>
          </div>
        </div>

        {/* Navegación principal */}
        <nav className="flex items-center gap-1 bg-zinc-950/50 border border-zinc-800/80 rounded-xl p-1">
          <Link
            href="/"
            className={`px-3 py-1.5 rounded-lg text-xs font-medium transition-colors ${
              pathname === "/"
                ? "bg-zinc-800 text-white"
                : "text-zinc-400 hover:text-zinc-200"
            }`}
          >
            Panel
          </Link>
          <Link
            href="/groups"
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-colors ${
              pathname === "/groups"
                ? "bg-zinc-800 text-white"
                : "text-zinc-400 hover:text-zinc-200"
            }`}
          >
            <Users className="w-3.5 h-3.5" />
            Grupos
          </Link>
        </nav>

        {/* Status & Actions */}
        <div className="flex items-center gap-4">
          {/* Usuario y rol */}
          <div className="hidden md:flex items-center gap-2 px-3 py-1.5 rounded-full bg-zinc-950/50 border border-zinc-800/80">
            {isAdmin ? (
              <ShieldCheck className="w-3.5 h-3.5 text-indigo-400" />
            ) : (
              <Eye className="w-3.5 h-3.5 text-zinc-400" />
            )}
            <span className="text-xs text-zinc-200 max-w-[120px] truncate">
              {user?.displayName || user?.username}
            </span>
            <span
              className={`text-[10px] px-1.5 py-0.5 rounded font-semibold ${
                isAdmin
                  ? "bg-indigo-500/15 text-indigo-300"
                  : "bg-zinc-700/50 text-zinc-300"
              }`}
            >
              {isAdmin ? "ADMIN" : "VIEWER"}
            </span>
          </div>

          {/* SignalR Live Badge */}
          <div
            className={`flex items-center gap-2 px-3 py-1.5 rounded-full text-xs font-medium border backdrop-blur-sm transition-colors ${
              connectionStatus === "connected"
                ? "bg-emerald-500/10 border-emerald-500/30 text-emerald-400"
                : connectionStatus === "reconnecting"
                ? "bg-amber-500/10 border-amber-500/30 text-amber-400"
                : "bg-rose-500/10 border-rose-500/30 text-rose-400"
            }`}
          >
            <Radio
              className={`w-3.5 h-3.5 ${
                connectionStatus === "connected" ? "animate-pulse" : ""
              }`}
            />
            <span className="capitalize">
              {connectionStatus === "connected"
                ? "SignalR En Vivo"
                : connectionStatus === "reconnecting"
                ? "Reconectando..."
                : "Desconectado"}
            </span>
          </div>

          {/* Refresh Button */}
          <button
            onClick={onRefresh}
            disabled={isLoading}
            className="flex items-center gap-2 px-3 py-1.5 rounded-lg bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium border border-zinc-700/80 transition-all active:scale-95 disabled:opacity-50"
            title="Refrescar pantallas manualmente"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? "animate-spin" : ""}`} />
            <span className="hidden sm:inline">Refrescar</span>
          </button>

          {/* Logout */}
          <button
            onClick={() => void signOut()}
            className="flex items-center gap-2 px-3 py-1.5 rounded-lg bg-zinc-800 hover:bg-rose-600/80 text-zinc-200 hover:text-white text-xs font-medium border border-zinc-700/80 transition-all active:scale-95"
            title="Cerrar sesión"
          >
            <LogOut className="w-3.5 h-3.5" />
            <span className="hidden lg:inline">Salir</span>
          </button>
        </div>
      </div>
    </header>
  );
}
