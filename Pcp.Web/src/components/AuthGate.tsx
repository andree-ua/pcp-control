"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "next/navigation";
import { Loader2 } from "lucide-react";
import { useAuth } from "@/context/AuthContext";

/**
 * Bloquea el panel hasta validar la sesión. El middleware hace la redirección
 * rápida con la cookie marcadora; este componente valida contra /api/auth/me.
 */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const { user, loading } = useAuth();
  const pathname = usePathname();
  const router = useRouter();
  const isLoginPage = pathname === "/login";

  useEffect(() => {
    if (!loading && !user && !isLoginPage) {
      router.replace("/login");
    }
  }, [loading, user, isLoginPage, router]);

  if (isLoginPage) {
    return <>{children}</>;
  }

  if (loading || !user) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-zinc-950 text-zinc-300">
        <div className="flex items-center gap-3 text-sm">
          <Loader2 className="w-5 h-5 animate-spin text-indigo-400" />
          Validando sesión...
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
