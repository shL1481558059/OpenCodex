FROM node:22-bookworm-slim AS frontend-build

WORKDIR /src/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build

WORKDIR /src

COPY opencodex_proxy/global.json ./
COPY opencodex_proxy/src/Libraries/OpenCodex.Domain/OpenCodex.Domain.csproj ./src/Libraries/OpenCodex.Domain/
COPY opencodex_proxy/src/Libraries/OpenCodex.Core/OpenCodex.Core.csproj ./src/Libraries/OpenCodex.Core/
COPY opencodex_proxy/src/Libraries/OpenCodex.Data/OpenCodex.Data.csproj ./src/Libraries/OpenCodex.Data/
COPY opencodex_proxy/src/Libraries/OpenCodex.CoreBase/OpenCodex.CoreBase.csproj ./src/Libraries/OpenCodex.CoreBase/
COPY opencodex_proxy/src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj ./src/Presentation/OpenCodex.Api/
RUN dotnet restore ./src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj

COPY opencodex_proxy/src ./src
RUN dotnet publish ./src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=dotnet-build /app/publish ./
COPY --from=frontend-build /src/frontend/dist/admin ./wwwroot/admin
COPY .env.example ./.env.example

ENTRYPOINT ["dotnet", "OpenCodex.Api.dll"]
