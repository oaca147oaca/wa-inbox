# ==========================
# STAGE 1: Build
# ==========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiamos el código
COPY . .

# Publicamos la app en modo Release a la carpeta /out
RUN dotnet publish -c Release -o /out

# ==========================
# STAGE 2: Runtime
# ==========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copiamos lo generado en la etapa anterior
COPY --from=build /out .

# Render expone el puerto dinámicamente con $PORT
ENV ASPNETCORE_URLS=http://+:$PORT
EXPOSE 8080

# Arrancamos la aplicación
ENTRYPOINT ["dotnet", "WaInbox.dll"]
