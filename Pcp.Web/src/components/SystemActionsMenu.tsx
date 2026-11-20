"use client";

import { useEffect, useRef, useState } from "react";
import {
  ChevronDown,
  Loader2,
  Lock,
  MonitorOff,
  Power,
  RotateCcw,
  Settings2,
  type LucideIcon,
} from "lucide-react";
import { sendScreenCommand } from "@/services/api";
import { useAuth } from "@/context/AuthContext";

interface SystemActionsMenuProps {
  screenId: string;
  isOnline: boolean;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

interface SystemAction {
  type: string;
  label: string;
  icon: LucideIcon;
  confirmMessage?: string;
  danger?: boolean;
}

const actions: SystemAction[] = [
  {
    type: "shutdown",
    label: "Apagar PC",
    icon: Power,
    danger: true,
    confirmMessage: "¿Apagar esta PC? Se cerrará Windows y la pantalla quedará sin servicio.",
  },
  {
    type: "restart",
    label: "Reiniciar PC",
    icon: RotateCcw,
    confirmMessage: "¿Reiniciar esta PC? La pantalla se desconectará temporalmente.",
  },
  {
    type: "monitor-off",
    label: "Apagar monitor",
    icon: MonitorOff,
  },
  {
    type: "lock",
    label: "Bloquear sesión",
    icon: Lock,
  },
];

export function SystemActionsMenu({
  screenId,
  isOnline,
  onSuccess,
  onError,
}: SystemActionsMenuProps) {
  const { isAdmin } = useAuth();
  const [open, setOpen] = useState(false);
  const [busyType, setBusyType] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;

    const handleClick = (event: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };

    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [open]);

  const execute = async (action: SystemAction) => {
    if (!isOnline || busyType !== null) return;

    if (action.confirmMessage && !window.confirm(action.confirmMessage)) {
      return;
    }

    setOpen(false);
    setBusyType(action.type);

    try {
      await sendScreenCommand(screenId, { type: action.type });
      onSuccess("Acción de sistema", `${action.label} enviada a [${screenId}].`);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo ejecutar la acción";
      onError("Acción de sistema", message);
    } finally {
      setBusyType(null);
    }
  };

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        disabled={!isOnline || !isAdmin || busyType !== null}
        className="w-full flex items-center justify-center gap-1.5 py-2 px-3 rounded-xl font-medium text-xs bg-zinc-800 hover:bg-zinc-700 text-zinc-200 transition-all active:scale-95 disabled:opacity-40 disabled:pointer-events-none border border-zinc-700/80"
        title={isAdmin ? "Acciones de sistema" : "Solo administradores"}
      >
        {busyType ? (
          <Loader2 className="w-3.5 h-3.5 animate-spin" />
        ) : (
          <Settings2 className="w-3.5 h-3.5" />
        )}
        <span>Sistema</span>
        <ChevronDown className={`w-3 h-3 transition-transform ${open ? "rotate-180" : ""}`} />
      </button>

      {open && (
        <div className="absolute right-0 top-full mt-2 w-56 z-30 rounded-xl border border-zinc-700 bg-zinc-900 shadow-2xl shadow-black/60 overflow-hidden">
          {actions.map((action) => {
            const Icon = action.icon;
            return (
              <button
                key={action.type}
                type="button"
                onClick={() => void execute(action)}
                className={`w-full flex items-center gap-2.5 px-3 py-2.5 text-left text-xs transition-colors ${
                  action.danger
                    ? "text-rose-300 hover:bg-rose-500/15"
                    : "text-zinc-200 hover:bg-zinc-800"
                }`}
              >
                <Icon className="w-4 h-4" />
                <span>{action.label}</span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
