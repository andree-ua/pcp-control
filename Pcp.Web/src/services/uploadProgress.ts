import type { FileUploadProgress } from "@/types/files";

/**
 * Bus de progreso de subidas.
 *
 * El evento SignalR llega a la conexión global del panel (useScreensSignalR), pero el
 * explorador de archivos está anidado en la tarjeta de pantalla y no tiene acceso al
 * HubConnection. Este módulo desacopla ambos lados: el hook publica y el modal se
 * suscribe solo al identificador de su propia subida.
 */

type UploadProgressListener = (event: FileUploadProgress) => void;

const listeners = new Map<string, Set<UploadProgressListener>>();

export function subscribeUploadProgress(
  clientRequestId: string,
  listener: UploadProgressListener
): () => void {
  let group = listeners.get(clientRequestId);
  if (!group) {
    group = new Set();
    listeners.set(clientRequestId, group);
  }

  group.add(listener);

  return () => {
    const current = listeners.get(clientRequestId);
    if (!current) return;
    current.delete(listener);
    if (current.size === 0) {
      listeners.delete(clientRequestId);
    }
  };
}

export function publishUploadProgress(event: FileUploadProgress): void {
  const group = listeners.get(event.clientRequestId);
  if (!group) return;

  group.forEach((listener) => {
    try {
      listener(event);
    } catch {
      // Un suscriptor roto no debe afectar a los demás.
    }
  });
}
