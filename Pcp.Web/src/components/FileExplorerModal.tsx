"use client";

import { useCallback, useEffect, useRef, useState, type ChangeEvent } from "react";
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
import { deleteFile, fetchFiles, sendScreenCommand, uploadFile } from "@/services/api";

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
  const [uploadProgress, setUploadProgress] = useState("");

  const fileInputRef = useRef<HTMLInputElement>(null);
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

  const handleUploadSelected = async (event: ChangeEvent<HTMLInputElement>) => {
    const selected = Array.from(event.target.files ?? []);
    event.target.value = "";

    if (selected.length === 0 || !currentPath) {
      return;
    }

    setUploading(true);
    setActionError(null);

    try {
      for (let index = 0; index < selected.length; index += 1) {
        setUploadProgress(`${index + 1}/${selected.length}`);
        await uploadFile(screenId, currentPath, selected[index]);
      }

      onSuccessRef.current(
        "Archivos subidos",
        `${selected.length} archivo(s) subido(s) a la carpeta actual.`
      );
      await load(currentPath);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : "No se pudo subir el archivo";
      setActionError(message);
      onErrorRef.current("Subir archivo", message);
      await load(currentPath);
    } finally {
      setUploading(false);
      setUploadProgress("");
    }
  };

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
            <span>{uploading ? `Subiendo ${uploadProgress}` : "Subir"}</span>
          </button>
        </div>

        {/* Error */}
        {actionError && (
          <div className="mx-5 mt-3 px-3 py-2 rounded-lg bg-rose-950/50 border border-rose-800/60 text-rose-300 text-xs flex items-start gap-2">
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <span className="break-words">{actionError}</span>
          </div>
        )}

        {/* Contenido */}
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

        {/* Pie */}
        <div className="px-5 py-2.5 border-t border-zinc-800 flex items-center justify-between text-[11px] text-zinc-500">
          <span>
            {atRoots
              ? `${listing?.roots.length ?? 0} unidad(es)`
              : `${entries.length} elemento(s)`}
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
