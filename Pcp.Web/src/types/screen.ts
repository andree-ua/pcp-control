export interface ScreenInfo {
  id: string;
  name: string;
  groupId?: string | null;
  groupName?: string | null;
  status: "online" | "offline" | string;
  lastHeartbeat: string;
  currentMedia: string;
  volume: number;
  cpuPercent: number;
  ramPercent: number;
  freeDiskMb: number;
  thumbnailBase64?: string;
  remoteControlAvailable?: boolean;
  macAddress?: string | null;
  localIp?: string | null;
  lastConnectionAt?: string | null;
  wolEnabled?: boolean;
  isWired?: boolean | null;
}

/**
 * Datos que devuelve el servidor para abrir una sesión noVNC.
 * La contraseña es efímera y solo vive lo que dura la sesión de control.
 */
export interface RemoteSession {
  sessionId: string;
  screenId: string;
  webSocketUrl: string;
  password: string;
  viewOnly: boolean;
  expiresInSeconds: number;
}

export interface ThumbnailDto {
  screenId: string;
  thumbnailBase64: string;
  timestamp: string;
}

export interface WakeResponse {
  requested: boolean;
  mode: "peer" | "direct" | string;
  relayScreenId?: string | null;
  macAddress: string;
  broadcastAddress?: string | null;
  commandId?: string | null;
  retryAfterSeconds: number;
}

export interface CommandRequest {
  type: string;
  payload?: Record<string, unknown>;
}

export interface CommandResponse {
  commandId: string;
  status: string;
  screenId: string;
  type: string;
}

export interface CommandResultDto {
  commandId: string;
  screenId: string;
  status: "ok" | "error" | string;
  detail?: string;
  timestamp: string;
}

export interface ToastMessage {
  id: string;
  title: string;
  message: string;
  type: "success" | "error" | "info";
}
