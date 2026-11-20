/**
 * Datos que devuelve el servidor para publicar la pantalla por WHIP a MediaMTX
 * (Bloque 5). Los tokens de publicación y lectura son efímeros y viven solo lo
 * que dura la sesión del proyector.
 */
export interface ProjectorSession {
  sessionId: string;
  screenId: string;
  streamPath: string;
  /** URL WHIP con el token de publicación (la usa el navegador del operador). */
  whipUrl: string;
  /** Token de publicación para el header Authorization: Bearer del POST WHIP. */
  whipToken: string;
  /** URL RTSP con credenciales de lectura (la usa el agente). */
  rtspUrl: string;
  /** URL HLS con credenciales de lectura (respaldo del agente). */
  hlsUrl: string;
  bufferMs: number;
  volume: number;
  expiresInSeconds: number;
}

export interface ProjectorActionResponse {
  sessionId: string;
  screenId: string;
  status: "live" | "closed" | "error" | string;
  detail?: string | null;
}

export interface ProjectorStatusResponse {
  active: boolean;
  sessionId?: string | null;
  screenId: string;
  streamPath?: string | null;
  isLive: boolean;
  detail?: string | null;
  expiresAt?: string | null;
}

/** Estado que el agente reporta por SignalR al abrir o cerrar la emisión. */
export interface ProjectorStatusDto {
  sessionId: string;
  screenId: string;
  success: boolean;
  detail?: string | null;
  protocol?: string | null;
}
