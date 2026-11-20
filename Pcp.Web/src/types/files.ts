export interface FileEntry {
  name: string;
  fullPath: string;
  isDirectory: boolean;
  sizeBytes: number;
  lastModified: string;
  extension: string;
  isVideo: boolean;
}

export interface FileListing {
  path: string;
  parentPath?: string | null;
  roots: string[];
  entries: FileEntry[];
  isRoot: boolean;
  truncated?: boolean;
}

export interface FileOperationResponse {
  success: boolean;
  savedPath?: string;
}

export type UploadPhase = "uploading" | "completed" | "failed";

/** Progreso del tramo servidor → agente (evento SignalR FileUploadProgress). */
export interface FileUploadProgress {
  clientRequestId: string;
  screenId: string;
  fileName: string;
  sentBytes: number;
  totalBytes: number;
  chunkIndex: number;
  phase: UploadPhase;
  error?: string | null;
}
