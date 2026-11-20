"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";
import { useRouter } from "next/navigation";
import { AuthUser, LoginResponse } from "@/types/auth";
import { fetchMe, login as loginRequest, logout as logoutRequest, clearSessionMarker } from "@/services/auth";

interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  isAdmin: boolean;
  signIn: (username: string, password: string) => Promise<LoginResponse>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const router = useRouter();

  useEffect(() => {
    let cancelled = false;

    fetchMe()
      .then((current) => {
        if (!cancelled) {
          setUser(current);
          if (!current) {
            // La cookie marcadora puede sobrevivir a una sesión caducada.
            clearSessionMarker();
          }
        }
      })
      .catch(() => {
        if (!cancelled) setUser(null);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  // Cualquier 401 de la API dispara este evento: la sesión se invalida y el
  // AuthGate redirige al login sin que cada componente tenga que gestionarlo.
  useEffect(() => {
    const handleUnauthorized = () => {
      clearSessionMarker();
      setUser(null);
      router.replace("/login");
    };

    window.addEventListener("pcp:unauthorized", handleUnauthorized);
    return () => window.removeEventListener("pcp:unauthorized", handleUnauthorized);
  }, [router]);

  const signIn = useCallback(async (username: string, password: string) => {
    const response = await loginRequest(username, password);
    setUser(response.user);
    return response;
  }, []);

  const signOut = useCallback(async () => {
    await logoutRequest();
    setUser(null);
    router.replace("/login");
  }, [router]);

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      loading,
      isAdmin: (user?.role ?? "").toLowerCase() === "admin",
      signIn,
      signOut,
    }),
    [user, loading, signIn, signOut]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth debe usarse dentro de AuthProvider");
  }
  return context;
}
