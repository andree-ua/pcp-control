/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  // Permite compilar a una carpeta distinta sin detener el servidor de desarrollo.
  distDir: process.env.NEXT_DIST_DIR || ".next",
  // Salida autocontenida para desplegar en la nube (node .next/standalone/server.js).
  output: "standalone",
};

export default nextConfig;
