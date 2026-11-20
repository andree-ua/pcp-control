import { CommandRequest, CommandResponse, RemoteSession, ScreenInfo } from "@/types/screen";
import { FileListing, FileOperationResponse } from "@/types/files";
import { GroupCommandResponse, GroupInfo, GroupUpsert } from "@/types/group";

// NEXT_PUBLIC_* se inyecta en tiempo de build (Next.js la incrusta en el bundle).
// En producción se define en Pcp.Web/.env.production o en el panel de la nube.
const configuredApiUrl = (
  process.env.NEXT_PUBLIC_API_URL ||
  process.env.NEXT_PUBLIC_SERVER_URL ||
  ""
).trim();

if (!configuredApiUrl && process.env.NODE_ENV === "production") {
  console.error(
    "[PCP] NEXT_PUBLIC_API_URL no está definida. El panel usará http://localhost:5000, " +
      "lo que no funcionará en producción."
  );
}

export const SERVER_URL = (configuredApiUrl || "http://localhost:5000").replace(/\/+$/, "");

export async function fetchScreens(): Promise<ScreenInfo[]> {
  const response = await fetch(`${SERVER_URL}/api/screens`, {
    cache: "no-store",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
    },
  });

  if (!response.ok) {
    throw new Error(`Error ${response.status}: No se pudo obtener la lista de pantallas`);
  }

  return response.json();
}

export async function sendScreenCommand(
  screenId: string,
  request: CommandRequest
): Promise<CommandResponse> {
  const response = await fetch(`${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/command`, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
  });

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    const errorMsg = data?.error || data?.message || `Fallo al enviar comando (HTTP ${response.status})`;
    throw new Error(errorMsg);
  }

  return data;
}

export async function fetchFiles(screenId: string, path?: string): Promise<FileListing> {
  const params = new URLSearchParams();
  if (path) {
    params.set("path", path);
  }

  const query = params.toString();
  const response = await fetch(
    `${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/files${query ? `?${query}` : ""}`,
    {
      cache: "no-store",
      credentials: "include",
    }
  );

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo listar la carpeta (HTTP ${response.status})`);
  }

  return data as FileListing;
}

export async function deleteFile(screenId: string, path: string): Promise<void> {
  const response = await fetch(
    `${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/files?path=${encodeURIComponent(path)}`,
    {
      method: "DELETE",
      credentials: "include",
    }
  );

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo eliminar el archivo (HTTP ${response.status})`);
  }
}

export async function uploadFile(
  screenId: string,
  directoryPath: string,
  file: File
): Promise<FileOperationResponse> {
  const formData = new FormData();
  formData.append("file", file);

  const response = await fetch(
    `${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/files/upload?path=${encodeURIComponent(directoryPath)}`,
    {
      method: "POST",
      credentials: "include",
      body: formData,
    }
  );

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo subir el archivo (HTTP ${response.status})`);
  }

  return data as FileOperationResponse;
}

export async function fetchGroups(): Promise<GroupInfo[]> {
  const response = await fetch(`${SERVER_URL}/api/groups`, {
    cache: "no-store",
    credentials: "include",
  });

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudieron cargar los grupos (HTTP ${response.status})`);
  }

  return data as GroupInfo[];
}

export async function createGroup(payload: GroupUpsert): Promise<GroupInfo> {
  const response = await fetch(`${SERVER_URL}/api/groups`, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(payload),
  });

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo crear el grupo (HTTP ${response.status})`);
  }

  return data as GroupInfo;
}

export async function updateGroup(groupId: string, payload: GroupUpsert): Promise<GroupInfo> {
  const response = await fetch(`${SERVER_URL}/api/groups/${encodeURIComponent(groupId)}`, {
    method: "PUT",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(payload),
  });

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo actualizar el grupo (HTTP ${response.status})`);
  }

  return data as GroupInfo;
}

export async function deleteGroup(groupId: string): Promise<void> {
  const response = await fetch(`${SERVER_URL}/api/groups/${encodeURIComponent(groupId)}`, {
    method: "DELETE",
    credentials: "include",
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error || `No se pudo eliminar el grupo (HTTP ${response.status})`);
  }
}

export async function sendGroupCommand(
  groupId: string,
  request: CommandRequest
): Promise<GroupCommandResponse> {
  const response = await fetch(
    `${SERVER_URL}/api/groups/${encodeURIComponent(groupId)}/command`,
    {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(request),
    }
  );

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo enviar el comando al grupo (HTTP ${response.status})`);
  }

  return data as GroupCommandResponse;
}

/**
 * Abre una sesión de escritorio remoto en el servidor.
 * El servidor prepara el túnel VNC del agente y devuelve la URL WebSocket para noVNC.
 */
export async function startRemoteSession(
  screenId: string,
  viewOnly = false
): Promise<RemoteSession> {
  const response = await fetch(
    `${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/remote/start`,
    {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ viewOnly }),
    }
  );

  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error || `No se pudo abrir el control remoto (HTTP ${response.status})`);
  }

  return data as RemoteSession;
}

/**
 * Ajusta la URL del túnel noVNC a la ubicación real del panel:
 *  - usa wss:// cuando la página se sirve por HTTPS (el navegador bloquea ws://);
 *  - corrige el caso en que el servidor anunció "localhost" y el panel se abrió
 *    desde otro equipo, donde ese "localhost" apuntaría al operador y no al servidor.
 * El resto de la ruta se respeta para no romper despliegues tras un proxy (PathBase).
 */
export function resolveVncSocketUrl(session: RemoteSession): string {
  const raw = session.webSocketUrl;

  if (typeof window === "undefined") {
    return raw;
  }

  try {
    const url = new URL(raw);
    const loopbackNames = new Set(["localhost", "127.0.0.1", "::1", "[::1]"]);

    if (loopbackNames.has(url.hostname) && !loopbackNames.has(window.location.hostname)) {
      const api = new URL(SERVER_URL, window.location.href);
      url.host = api.host;
    }

    if (window.location.protocol === "https:" && url.protocol === "ws:") {
      url.protocol = "wss:";
    }
    else if (window.location.protocol === "http:" && url.protocol === "wss:") {
      url.protocol = "ws:";
    }

    return url.toString();
  } catch {
    return raw;
  }
}

/**
 * Cierra la sesión de escritorio remoto y libera el VNC del agente.
 */
export async function stopRemoteSession(screenId: string, sessionId?: string): Promise<void> {
  try {
    await fetch(`${SERVER_URL}/api/screens/${encodeURIComponent(screenId)}/remote/stop`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ sessionId: sessionId ?? null }),
      keepalive: true,
    });
  } catch {
    // El cierre es best-effort: el servidor cierra la sesión al caer el WebSocket.
  }
}
