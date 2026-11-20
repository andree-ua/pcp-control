"use client";

import { ToastMessage } from "@/types/screen";
import { CheckCircle2, AlertCircle, Info, X } from "lucide-react";

interface ToastProps {
  toasts: ToastMessage[];
  onDismiss: (id: string) => void;
}

export function ToastContainer({ toasts, onDismiss }: ToastProps) {
  if (toasts.length === 0) return null;

  return (
    <div className="fixed bottom-5 right-5 z-50 flex flex-col gap-2 max-w-sm w-full pointer-events-none">
      {toasts.map((toast) => {
        const isSuccess = toast.type === "success";
        const isError = toast.type === "error";

        return (
          <div
            key={toast.id}
            className={`pointer-events-auto flex items-start gap-3 p-4 rounded-xl border shadow-xl backdrop-blur-md transition-all duration-300 animate-in slide-in-from-bottom-5 ${
              isSuccess
                ? "bg-emerald-950/90 border-emerald-500/40 text-emerald-200"
                : isError
                ? "bg-rose-950/90 border-rose-500/40 text-rose-200"
                : "bg-zinc-900/90 border-zinc-700/50 text-zinc-200"
            }`}
          >
            <div className="mt-0.5 flex-shrink-0">
              {isSuccess && <CheckCircle2 className="w-5 h-5 text-emerald-400" />}
              {isError && <AlertCircle className="w-5 h-5 text-rose-400" />}
              {!isSuccess && !isError && <Info className="w-5 h-5 text-blue-400" />}
            </div>

            <div className="flex-1 min-w-0">
              <h4 className="text-sm font-semibold leading-tight text-white">{toast.title}</h4>
              <p className="text-xs mt-1 opacity-90 break-words">{toast.message}</p>
            </div>

            <button
              onClick={() => onDismiss(toast.id)}
              className="flex-shrink-0 text-zinc-400 hover:text-white transition-colors"
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
