# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Etapa 1: compilación
# El contexto de build debe ser la RAÍZ del repositorio (donde está Pcp.sln),
# porque Pcp.Server referencia a Pcp.Shared. En Render:
#   Dockerfile Path = Pcp.Server/Dockerfile
#   Root Directory  = (vacío)
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar solo los .csproj para aprovechar la caché de capas de Docker.
COPY ["Pcp.Server/Pcp.Server.csproj", "Pcp.Server/"]
COPY ["Pcp.Shared/Pcp.Shared.csproj", "Pcp.Shared/"]

# Restaurar dependencias (incluye Pomelo MySQL, EF Core, SignalR, etc.).
RUN dotnet restore "Pcp.Server/Pcp.Server.csproj"

# Copiar el resto del código de ambos proyectos.
COPY Pcp.Server/ Pcp.Server/
COPY Pcp.Shared/ Pcp.Shared/

# Publicar en Release.
WORKDIR /src/Pcp.Server
RUN dotnet publish "Pcp.Server.csproj" \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────
# Etapa 2: ejecución
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Render inyecta PORT; Program.cs lo usa para escuchar en 0.0.0.0:{PORT}.
# Si PORT no existe, se usa el 8080 definido aquí.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Pcp.Server.dll"]
