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
