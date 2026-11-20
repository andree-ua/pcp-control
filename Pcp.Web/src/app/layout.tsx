import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "PCP - Panel de Control de Pantallas",
  description: "Administración y control remoto en tiempo real de pantallas con SignalR",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="es" className="dark">
      <body className="bg-zinc-950 text-zinc-100 antialiased selection:bg-indigo-500/30 selection:text-indigo-200">
        {children}
      </body>
    </html>
  );
}
