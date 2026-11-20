import { AuthUser, LoginResponse } from "@/types/auth";
import { SERVER_URL } from "@/services/api";

const SESSION_MARKER = "pcp_session";

/**
 * Marca no sensible para que el middleware de Next pueda redirigir sin acceso a
 * la cookie HttpOnly (que pertenece al dominio de la API). No contiene el JWT.
 */
export function setSessionMarker(): void {
  if (typeof document === "undefined") return;

  const secure = window.location.protocol === "https:" ? "; Secure" : "";
  document.cookie = `${SESSION_MARKER}=1; Path=/; SameSite=Lax; Max-Age=${60 * 60 * 8}${secure}`;
}

export function clearSessionMarker(): void {
  if (typeof document === "undefined") return;
  document.cookie = `${SESSION_MARKER}=; Path=/; SameSite=Lax; Max-Age=0`;
}

export async function login(username: string, password: string): Promise<LoginResponse> {
  const response = await fetch(`${SERVER_URL}/api/auth/login`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo iniciar sesión (HTTP ${response.status})`);
  }

  setSessionMarker();
  return data as LoginResponse;
}

export async function logout(): Promise<void> {
  try {
    await fetch(`${SERVER_URL}/api/auth/logout`, {
      method: "POST",
      credentials: "include",
    });
  } finally {
    clearSessionMarker();
  }
}

/**
 * Devuelve el usuario de la sesión o null si no hay cookie válida.
 * No redirige ni lanza: el proveedor de autenticación decide qué hacer.
 */
export async function fetchMe(): Promise<AuthUser | null> {
  const response = await fetch(`${SERVER_URL}/api/auth/me`, {
    cache: "no-store",
    credentials: "include",
  });

  if (response.status === 401) {
    return null;
  }

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo validar la sesión (HTTP ${response.status})`);
  }

  return data as AuthUser;
}
