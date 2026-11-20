"use client";

import { useCallback, useEffect, useRef, useState, type ChangeEvent, type DragEvent } from "react";
import {
  AlertTriangle,
  ChevronLeft,
  File as FileIcon,
  Film,
  Folder,
  FolderOpen,
  HardDrive,
  Loader2,
  Play,
  RefreshCw,
  Trash2,
  Upload,
  X,
} from "lucide-react";
import { FileEntry, FileListing } from "@/types/files";
import { deleteFile, fetchFiles, sendScreenCommand, uploadFileWithProgress } from "@/services/api";
import { subscribeUploadProgress } from "@/services/uploadProgress";

interface FileExplorerModalProps {
  open: boolean;
  screenId: string;
  screenName?: string;
  onClose: () => void;
  onSuccess: (title: string, message: string) => void;
  onError: (title: string, message: string) => void;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function formatDate(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

type UploadStage = "sending" | "delivering" | "verifying";

interface UploadState {
  fileName: string;
  index: number;
  count: number;
  loaded: number;
  total: number;
  stage: UploadStage;
  rateBytesPerSecond: number;
}

export function FileExplorerModal({
  open,
  screenId,
  screenName,
  onClose,
  onSuccess,
  onError,
}: FileExplorerModalProps) {
  const [listing, setListing] = useState<FileListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [busyPath, setBusyPath] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadState, setUploadState] = useState<UploadState | null>(null);
  const [dragActive, setDragActive] = useState(false);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const uploadAbortRef = useRef<AbortController | null>(null);
  const dragDepthRef = useRef(0);
  const onCloseRef = useRef(onClose);
  const onSuccessRef = useRef(onSuccess);
  const onErrorRef = useRef(onError);

  useEffect(() => {
    onCloseRef.current = onClose;
    onSuccessRef.current = onSuccess;
    onErrorRef.current = onError;
  }, [onClose, onSuccess, onError]);

  const load = useCallback(
    async (path?: string) => {
      setLoading(true);
      setActionError(null);

      try {
        const data = await fetchFiles(screenId, path);
        setListing(data);
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : "Error al cargar archivos";
        setActionError(message);
        onErrorRef.current("Explorador de archivos", message);
      } finally {
        setLoading(false);
      }
    },
    [screenId]
  );

  useEffect(() => {
    if (!open) return;

    setListing(null);
    setActionError(null);
    void load();
  }, [open, load]);

  useEffect(() => {
    if (!open) return;

    const handleKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        onCloseRef.current();
      }
    };

    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [open]);

  // Cerrar el modal cancela la subida en curso (el servidor avisa al agente y este
  // elimina el archivo temporal).
  useEffect(() => {
    if (!open) {
      uploadAbortRef.current?.abort();
    }
  }, [open]);

  if (!open) {
    return null;
  }

  const currentPath = listing?.path?.trim() ? listing.path : null;
  const atRoots = !currentPath;
  const entries = listing?.entries ?? [];
  const isBusy = busyPath !== null || uploading;

  const parts = currentPath
    ? currentPath.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean)
    : [];

  let accumulatedPath = "";
  const breadcrumbs = parts.map((part, index) => {
    accumulatedPath =
      index === 0
        ? `${part}\\`
        : `${accumulatedPath.replace(/[\\/]+$/, "")}\\${part}`;

    return { label: part, path: accumulatedPath };
  });

  const handleDelete = async (entry: FileEntry) => {
    if (!window.confirm(`¿Eliminar el archivo "${entry.name}"? Esta acción no se puede deshacer.`)) {
      return;
    }

    setBusyPath(entry.fullPath);

    try {
      await deleteFile(screenId, entry.fullPath);
      onSuccessRef.current("Archivo eliminado", `Se eliminó "${entry.name}".`);
      await load(currentPath ?? undefined);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo eliminar el archivo";
      onErrorRef.current("Eliminar archivo", message);
    } finally {
      setBusyPath(null);
    }
  };

  const handlePlay = async (entry: FileEntry) => {
    setBusyPath(entry.fullPath);

    try {
      await sendScreenCommand(screenId, {
        type: "play",
        payload: {
          mediaPath: entry.fullPath,
          loop: true,
          volume: 80,
          fitMode: "cover",
        },
      });
      onSuccessRef.current("Reproducción iniciada", `Reproduciendo "${entry.name}" en la pantalla.`);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo iniciar la reproducción";
      onErrorRef.current("Reproducir archivo", message);
    } finally {
      setBusyPath(null);
    }
  };

  const uploadFiles = useCallback(
    async (files: File[]) => {
      const folder = listing?.path?.trim() ? listing.path : null;
      if (!folder || files.length === 0 || uploading) {
        return;
      }

      // Un directorio arrastrado llega como File de tamaño 0 y sin tipo.
      const valid = files.filter((file) => file.size > 0);
      if (valid.length === 0) {
        const message = "No se admiten carpetas: arrastra archivos, no directorios.";
        setActionError(message);
        onErrorRef.current("Subir archivo", message);
        return;
      }

      setUploading(true);
      setActionError(null);

      const controller = new AbortController();
      uploadAbortRef.current = controller;

      try {
        for (let index = 0; index < valid.length; index += 1) {
          if (controller.signal.aborted) {
            throw new DOMException("Subida cancelada por el operador.", "AbortError");
          }

          const file = valid[index];
          const startedAt = Date.now();
          const baseState: UploadState = {
            fileName: file.name,
            index: index + 1,
            count: valid.length,
            loaded: 0,
            total: file.size,
            stage: "sending",
            rateBytesPerSecond: 0,
          };
          setUploadState(baseState);

          const handle = uploadFileWithProgress(screenId, folder, file, {
            signal: controller.signal,
            onBrowserProgress: (loaded, total) => {
              const elapsedSeconds = Math.max(0.001, (Date.now() - startedAt) / 1000);
              const effectiveTotal = total || file.size;
              setUploadState({
                ...baseState,
                loaded,
                total: effectiveTotal,
                stage: loaded >= effectiveTotal ? "delivering" : "sending",
                rateBytesPerSecond: loaded / elapsedSeconds,
              });
            },
          });

          // Tramo servidor -> agente: el servidor etiqueta cada evento con el id de esta subida.
          const unsubscribe = subscribeUploadProgress(handle.clientRequestId, (event) => {
            if (event.phase !== "uploading") {
              return;
            }

            setUploadState({
              ...baseState,
              loaded: event.sentBytes,
              total: event.totalBytes || file.size,
              stage: "delivering",
              rateBytesPerSecond: 0,
            });
          });

          try {
            await handle.promise;
            setUploadState((prev) =>
              prev ? { ...prev, loaded: prev.total, stage: "verifying", rateBytesPerSecond: 0 } : prev
            );
          } finally {
            unsubscribe();
          }
        }

        onSuccessRef.current(
          "Archivos subidos",
          `${valid.length} archivo(s) subido(s) a la carpeta actual.`
        );
        await load(folder);
      } catch (err: unknown) {
        const aborted = err instanceof DOMException && err.name === "AbortError";
        const message = aborted
          ? "Subida cancelada por el operador."
          : err instanceof Error
            ? err.message
            : "No se pudo subir el archivo";

        if (aborted) {
          onSuccessRef.current("Subida cancelada", message);
        } else {
          setActionError(message);
          onErrorRef.current("Subir archivo", message);
        }

        await load(folder);
      } finally {
        uploadAbortRef.current = null;
        setUploading(false);
        setUploadState(null);
      }
    },
    [screenId, listing, uploading, load]
  );

  const handleUploadSelected = (event: ChangeEvent<HTMLInputElement>) => {
    const selected = Array.from(event.target.files ?? []);
    event.target.value = "";
    void uploadFiles(selected);
  };

  const cancelUpload = () => {
    uploadAbortRef.current?.abort();
  };

  const canDrop = Boolean(currentPath) && !isBusy && !loading;

  const handleDragEnter = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    if (!canDrop) return;
    dragDepthRef.current += 1;
    setDragActive(true);
  };

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = canDrop ? "copy" : "none";
  };

  const handleDragLeave = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    if (!canDrop) return;
    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
    if (dragDepthRef.current === 0) {
      setDragActive(false);
    }
  };

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    dragDepthRef.current = 0;
    setDragActive(false);

    if (!currentPath) {
      const message = "Selecciona una carpeta (una unidad) antes de arrastrar archivos.";
      setActionError(message);
      onErrorRef.current("Subir archivo", message);
      return;
    }

    if (isBusy) {
      return;
    }

    void uploadFiles(Array.from(event.dataTransfer.files));
  };

  const uploadPercent =
    uploadState && uploadState.total > 0
      ? Math.min(100, Math.round((uploadState.loaded / uploadState.total) * 100))
      : 0;

  const uploadStageLabel =
    uploadState?.stage === "sending"
      ? "Enviando al servidor..."
      : uploadState?.stage === "delivering"
        ? "Entregando al agente..."
        : "Verificando en el agente...";

  const uploadEtaSeconds =
    uploadState && uploadState.stage === "sending" && uploadState.rateBytesPerSecond > 0
      ? Math.max(
          1,
          Math.ceil((uploadState.total - uploadState.loaded) / uploadState.rateBytesPerSecond)
        )
      : null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose();
        }
      }}
    >
      <div className="w-full max-w-4xl max-h-[86vh] bg-zinc-900 border border-zinc-700/80 rounded-2xl shadow-2xl flex flex-col overflow-hidden">
        {/* Cabecera */}
        <div className="flex items-start justify-between gap-4 px-5 py-4 border-b border-zinc-800">
          <div className="flex items-center gap-3 min-w-0">
            <div className="w-10 h-10 rounded-xl bg-indigo-500/15 border border-indigo-500/30 flex items-center justify-center text-indigo-400 flex-shrink-0">
              <FolderOpen className="w-5 h-5" />
            </div>
            <div className="min-w-0">
              <h2 className="text-base font-semibold text-white">Explorador de archivos</h2>
              <p className="text-xs text-zinc-400 truncate">
                {screenName || screenId} <span className="font-mono text-zinc-500">({screenId})</span>
              </p>
            </div>
          </div>

          <button
            type="button"
            onClick={onClose}
            className="p-2 rounded-lg text-zinc-400 hover:text-white hover:bg-zinc-800 transition-colors"
            title="Cerrar"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Barra de herramientas */}
        <div className="px-5 py-3 border-b border-zinc-800 flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => currentPath && void load(listing?.parentPath ?? undefined)}
            disabled={!listing?.parentPath || loading || isBusy}
            className="flex items-center gap-1 px-2.5 py-1.5 rounded-lg bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium border border-zinc-700/80 disabled:opacity-40 disabled:pointer-events-none transition-colors"
            title="Subir un nivel"
          >
            <ChevronLeft className="w-3.5 h-3.5" />
            Subir
          </button>

          <div className="flex-1 min-w-[180px] flex items-center gap-1 text-xs text-zinc-400 overflow-hidden">
            <button
              type="button"
              onClick={() => void load()}
              disabled={loading || isBusy}
              className="text-indigo-400 hover:text-indigo-300 font-medium disabled:opacity-50"
            >
              Unidades
            </button>
            {breadcrumbs.map((crumb) => (
              <span key={crumb.path} className="flex items-center gap-1 min-w-0">
                <span className="text-zinc-600">/</span>
                <button
                  type="button"
                  onClick={() => void load(crumb.path)}
                  disabled={loading || isBusy}
                  className="hover:text-zinc-200 truncate disabled:opacity-50"
                  title={crumb.path}
                >
                  {crumb.label}
                </button>
              </span>
            ))}
          </div>

          <button
            type="button"
            onClick={() => void load(currentPath ?? undefined)}
            disabled={loading || isBusy}
            className="p-2 rounded-lg text-zinc-300 hover:text-white hover:bg-zinc-800 border border-zinc-700/80 disabled:opacity-40 transition-colors"
            title="Actualizar"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? "animate-spin" : ""}`} />
          </button>

          <input
            ref={fileInputRef}
            type="file"
            multiple
            className="hidden"
            onChange={handleUploadSelected}
          />

          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            disabled={!currentPath || loading || isBusy}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-medium disabled:opacity-40 disabled:pointer-events-none transition-colors"
            title={currentPath ? "Subir archivos a la carpeta actual" : "Selecciona primero una carpeta"}
          >
            {uploading ? (
              <Loader2 className="w-3.5 h-3.5 animate-spin" />
            ) : (
              <Upload className="w-3.5 h-3.5" />
            )}
            <span>{uploading ? "Subiendo..." : "Subir"}</span>
          </button>
        </div>

        {/* Progreso real de la subida (navegador -> servidor -> agente) */}
        {uploading && uploadState && (
          <div className="px-5 py-3 border-b border-zinc-800 bg-zinc-950/50">
            <div className="flex items-center justify-between gap-3 text-xs">
              <span className="truncate font-medium text-zinc-200" title={uploadState.fileName}>
                {uploadState.index}/{uploadState.count} · {uploadState.fileName}
              </span>
              <span className="flex items-center gap-2 flex-shrink-0">
                <span className="font-mono text-indigo-300">{uploadPercent}%</span>
                <button
                  type="button"
                  onClick={cancelUpload}
                  className="px-2 py-0.5 rounded-md bg-zinc-800 hover:bg-rose-600 text-zinc-300 hover:text-white text-[10px] font-medium transition-colors"
                  title="Cancelar la subida en curso"
                >
                  Cancelar
                </button>
              </span>
            </div>

            <div className="mt-2 h-1.5 rounded-full bg-zinc-800 overflow-hidden">
              <div
                className={`h-full rounded-full bg-indigo-500 transition-[width] duration-200 ${
                  uploadState.stage === "verifying" ? "animate-pulse" : ""
                }`}
                style={{ width: `${Math.max(2, uploadPercent)}%` }}
              />
            </div>

            <div className="mt-1.5 flex items-center justify-between gap-3 text-[11px] text-zinc-500">
              <span className="truncate">{uploadStageLabel}</span>
              <span className="flex items-center gap-2 whitespace-nowrap">
                <span className="font-mono">
                  {formatSize(uploadState.loaded)} / {formatSize(uploadState.total)}
                </span>
                {uploadState.stage === "sending" && uploadState.rateBytesPerSecond > 0 && (
                  <span className="font-mono">{formatSize(uploadState.rateBytesPerSecond)}/s</span>
                )}
                {uploadEtaSeconds !== null && <span className="font-mono">~{uploadEtaSeconds}s</span>}
              </span>
            </div>
          </div>
        )}

        {/* Error */}
        {actionError && (
          <div className="mx-5 mt-3 px-3 py-2 rounded-lg bg-rose-950/50 border border-rose-800/60 text-rose-300 text-xs flex items-start gap-2">
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <span className="break-words">{actionError}</span>
          </div>
        )}

        {/* Contenido (zona de arrastrar y soltar) */}
        <div
          className="relative flex-1 min-h-0 flex flex-col"
          onDragEnter={handleDragEnter}
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
        >
          {dragActive && canDrop && (
            <div className="absolute inset-2 z-20 flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed border-indigo-400 bg-indigo-950/85 text-center pointer-events-none backdrop-blur-sm">
              <Upload className="w-8 h-8 text-indigo-300" />
              <p className="text-sm font-semibold text-indigo-100">Suelta los archivos para subirlos</p>
              <p className="max-w-[85%] truncate font-mono text-[11px] text-indigo-300/80">
                {currentPath}
              </p>
            </div>
          )}

          <div className="flex-1 overflow-auto min-h-[240px]">
          {loading && !listing ? (
            <div className="h-full min-h-[240px] flex flex-col items-center justify-center text-zinc-400 gap-3">
              <Loader2 className="w-7 h-7 animate-spin text-indigo-500" />
              <p className="text-xs font-medium">Leyendo archivos del agente...</p>
            </div>
          ) : atRoots ? (
            <div className="p-5">
              <p className="text-[11px] uppercase tracking-wider text-zinc-500 font-semibold mb-3">
                Unidades disponibles
              </p>
              <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-3">
                {(listing?.roots ?? []).map((root) => (
                  <button
                    key={root}
                    type="button"
                    onClick={() => void load(root)}
                    disabled={isBusy}
                    className="flex items-center gap-3 p-4 rounded-xl bg-zinc-950/60 border border-zinc-800 hover:border-indigo-500/50 hover:bg-zinc-800/40 text-left transition-colors disabled:opacity-50"
                  >
                    <HardDrive className="w-5 h-5 text-indigo-400 flex-shrink-0" />
                    <span className="font-mono text-sm text-zinc-200">{root}</span>
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <table className="w-full text-xs">
              <thead className="sticky top-0 bg-zinc-900/95 backdrop-blur z-10">
                <tr className="text-zinc-500 uppercase tracking-wider text-[10px]">
                  <th className="text-left font-semibold px-5 py-2.5">Nombre</th>
                  <th className="text-right font-semibold px-3 py-2.5 w-24">Tamaño</th>
                  <th className="text-left font-semibold px-3 py-2.5 w-44">Modificado</th>
                  <th className="text-right font-semibold px-5 py-2.5 w-24">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {entries.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="px-5 py-10 text-center text-zinc-500">
                      Carpeta vacía.
                    </td>
                  </tr>
                ) : (
                  entries.map((entry) => (
                    <tr
                      key={entry.fullPath}
                      className="border-t border-zinc-800/60 hover:bg-zinc-800/30 transition-colors"
                    >
                      <td className="px-5 py-2.5">
                        <button
                          type="button"
                          onClick={() => entry.isDirectory && void load(entry.fullPath)}
                          disabled={!entry.isDirectory || isBusy}
                          className={`flex items-center gap-2 min-w-0 max-w-full text-left ${
                            entry.isDirectory ? "hover:text-indigo-300" : "cursor-default"
                          } disabled:opacity-70`}
                          title={entry.fullPath}
                        >
                          {entry.isDirectory ? (
                            <Folder className="w-4 h-4 text-amber-400 flex-shrink-0" />
                          ) : entry.isVideo ? (
                            <Film className="w-4 h-4 text-violet-400 flex-shrink-0" />
                          ) : (
                            <FileIcon className="w-4 h-4 text-zinc-500 flex-shrink-0" />
                          )}
                          <span className="truncate text-zinc-200">{entry.name}</span>
                        </button>
                      </td>
                      <td className="px-3 py-2.5 text-right font-mono text-zinc-400">
                        {entry.isDirectory ? "—" : formatSize(entry.sizeBytes)}
                      </td>
                      <td className="px-3 py-2.5 text-zinc-400 whitespace-nowrap">
                        {formatDate(entry.lastModified)}
                      </td>
                      <td className="px-5 py-2.5">
                        <div className="flex items-center justify-end gap-1">
                          {entry.isVideo && !entry.isDirectory && (
                            <button
                              type="button"
                              onClick={() => void handlePlay(entry)}
                              disabled={isBusy}
                              className="p-1.5 rounded-lg text-emerald-400 hover:bg-emerald-500/15 disabled:opacity-40 transition-colors"
                              title="Reproducir en la pantalla"
                            >
                              {busyPath === entry.fullPath ? (
                                <Loader2 className="w-4 h-4 animate-spin" />
                              ) : (
                                <Play className="w-4 h-4" />
                              )}
                            </button>
                          )}
                          {!entry.isDirectory && (
                            <button
                              type="button"
                              onClick={() => void handleDelete(entry)}
                              disabled={isBusy}
                              className="p-1.5 rounded-lg text-zinc-400 hover:text-rose-400 hover:bg-rose-500/10 disabled:opacity-40 transition-colors"
                              title="Eliminar archivo"
                            >
                              {busyPath === entry.fullPath ? (
                                <Loader2 className="w-4 h-4 animate-spin" />
                              ) : (
                                <Trash2 className="w-4 h-4" />
                              )}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          )}
          </div>
        </div>

        {/* Pie */}
        <div className="px-5 py-2.5 border-t border-zinc-800 flex items-center justify-between text-[11px] text-zinc-500">
          <span>
            {atRoots
              ? `${listing?.roots.length ?? 0} unidad(es)`
              : `${entries.length} elemento(s)`}
            {!atRoots && !uploading ? " · arrastra archivos aquí" : ""}
            {listing?.truncated ? " · lista truncada" : ""}
          </span>
          <span className="font-mono truncate max-w-[60%]" title={currentPath ?? ""}>
            {currentPath ?? "Selecciona una unidad"}
          </span>
        </div>
      </div>
    </div>
  );
}
