/**
 * Tipos mínimos para noVNC (@novnc/novnc), que se distribuye sin declaraciones.
 * Cubre solo la superficie que usa el panel PCP.
 */
declare module "@novnc/novnc/lib/rfb" {
  export interface RFBOptions {
    shared?: boolean;
    credentials?: {
      username?: string;
      password?: string;
      target?: string;
    };
    repeaterID?: string;
    wsProtocols?: string[];
  }

  export default class RFB {
    constructor(
      target: HTMLElement,
      url: string | WebSocket,
      options?: RFBOptions
    );

    viewOnly: boolean;
    focusOnClick: boolean;
    clipViewport: boolean;
    scaleViewport: boolean;
    resizeSession: boolean;
    showDotCursor: boolean;
    qualityLevel: number;
    compressionLevel: number;
    background: string;

    disconnect(): void;
    sendCtrlAltDel(): void;
    sendCredentials(credentials: { username?: string; password?: string; target?: string }): void;
    sendKey(keysym: number, code: string | null, down?: boolean): void;
    focus(options?: FocusOptions): void;

    addEventListener(type: string, listener: (event: Event) => void): void;
    removeEventListener(type: string, listener: (event: Event) => void): void;
  }
}

declare module "@novnc/novnc/lib/util/logging" {
  export type LogLevel = "debug" | "info" | "warn" | "error";

  /** Configura el nivel de logs internos de noVNC en la consola del navegador. */
  export function initLogging(level?: LogLevel | string): void;
}
