"use client";

import { ScreenInfo } from "@/types/screen";
import { Monitor, Wifi, WifiOff, PlayCircle } from "lucide-react";

interface SummaryBarProps {
  screens: ScreenInfo[];
}

export function SummaryBar({ screens }: SummaryBarProps) {
  const total = screens.length;
  const online = screens.filter((s) => s.status.toLowerCase() === "online").length;
  const offline = total - online;
  const playing = screens.filter(
    (s) => s.status.toLowerCase() === "online" && s.currentMedia && s.currentMedia !== "idle"
  ).length;

  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
      <div className="bg-zinc-900/50 border border-zinc-800/80 rounded-2xl p-4 flex items-center gap-4 shadow-sm">
        <div className="w-12 h-12 rounded-xl bg-indigo-500/10 border border-indigo-500/20 flex items-center justify-center text-indigo-400">
          <Monitor className="w-6 h-6" />
        </div>
        <div>
          <p className="text-xs text-zinc-400 font-medium uppercase tracking-wider">Pantallas Totales</p>
          <p className="text-2xl font-bold text-white mt-0.5">{total}</p>
        </div>
      </div>

      <div className="bg-zinc-900/50 border border-zinc-800/80 rounded-2xl p-4 flex items-center gap-4 shadow-sm">
        <div className="w-12 h-12 rounded-xl bg-emerald-500/10 border border-emerald-500/20 flex items-center justify-center text-emerald-400">
          <Wifi className="w-6 h-6" />
        </div>
        <div>
          <p className="text-xs text-zinc-400 font-medium uppercase tracking-wider">Online</p>
          <p className="text-2xl font-bold text-emerald-400 mt-0.5">{online}</p>
        </div>
      </div>

      <div className="bg-zinc-900/50 border border-zinc-800/80 rounded-2xl p-4 flex items-center gap-4 shadow-sm">
        <div className="w-12 h-12 rounded-xl bg-rose-500/10 border border-rose-500/20 flex items-center justify-center text-rose-400">
          <WifiOff className="w-6 h-6" />
        </div>
        <div>
          <p className="text-xs text-zinc-400 font-medium uppercase tracking-wider">Offline</p>
          <p className="text-2xl font-bold text-rose-400 mt-0.5">{offline}</p>
        </div>
      </div>

      <div className="bg-zinc-900/50 border border-zinc-800/80 rounded-2xl p-4 flex items-center gap-4 shadow-sm">
        <div className="w-12 h-12 rounded-xl bg-violet-500/10 border border-violet-500/20 flex items-center justify-center text-violet-400">
          <PlayCircle className="w-6 h-6" />
        </div>
        <div>
          <p className="text-xs text-zinc-400 font-medium uppercase tracking-wider">En Reproducción</p>
          <p className="text-2xl font-bold text-violet-400 mt-0.5">{playing}</p>
        </div>
      </div>
    </div>
  );
}
