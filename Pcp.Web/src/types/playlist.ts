export interface PlaylistItem {
  id: string;
  position: number;
  mediaPath: string;
  /** 0 = hasta el fin natural del medio. */
  durationSeconds: number;
  /** 1-100; 0 = volumen por defecto de la playlist. */
  volume: number;
}

export interface Playlist {
  id: string;
  name: string;
  loop: boolean;
  createdAt: string;
  updatedAt: string;
  items: PlaylistItem[];
}

export interface PlaylistItemInput {
  mediaPath: string;
  durationSeconds: number;
  volume: number;
}

export interface PlaylistUpsert {
  name: string;
  loop: boolean;
  items: PlaylistItemInput[];
}

export interface PlaylistPlayRequest {
  screenIds?: string[];
  groupId?: string | null;
  startInSeconds?: number;
}

export interface PlaylistDispatchItem {
  screenId: string;
  success: boolean;
  commandId?: string | null;
  error?: string | null;
}

export interface PlaylistPlayResponse {
  playlistId: string;
  name: string;
  startAtUnixMs: number;
  startAtUtc: string;
  total: number;
  sent: number;
  offline: number;
  failed: number;
  results: PlaylistDispatchItem[];
}

/** Destino elegido en la tarjeta de playlist. */
export interface PlaylistTarget {
  groupId?: string;
  screenId?: string;
}
