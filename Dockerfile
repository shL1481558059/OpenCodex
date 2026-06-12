# ===== 前端构建阶段 =====
FROM node:22-bookworm-slim AS frontend-build

WORKDIR /src/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --omit=dev

COPY frontend ./
RUN npm run build

# ===== .NET 构建阶段 =====
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS dotnet-build

WORKDIR /src

COPY opencodex_proxy/global.json ./
COPY opencodex_proxy/src/Libraries/OpenCodex.Domain/OpenCodex.Domain.csproj ./src/Libraries/OpenCodex.Domain/
COPY opencodex_proxy/src/Libraries/OpenCodex.Core/OpenCodex.Core.csproj ./src/Libraries/OpenCodex.Core/
COPY opencodex_proxy/src/Libraries/OpenCodex.Data/OpenCodex.Data.csproj ./src/Libraries/OpenCodex.Data/
COPY opencodex_proxy/src/Libraries/OpenCodex.CoreBase/OpenCodex.CoreBase.csproj ./src/Libraries/OpenCodex.CoreBase/
COPY opencodex_proxy/src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj ./src/Presentation/OpenCodex.Api/

RUN dotnet restore ./src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj \
    --runtime linux-musl-x64

COPY opencodex_proxy/src ./src

RUN dotnet publish ./src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj \
    --configuration Release \
    --runtime linux-musl-x64 \
    --self-contained false \
    --no-restore \
    --output /app/publish

# 清理调试文件和文档
RUN cd /app/publish && \
    find . -name "*.pdb" -delete && \
    find . -name "*.xml" -delete && \
    find . -name "*.Development.*" -delete

# ===== 最终运行阶段 =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# 安装时区数据和可选依赖
RUN apk add --no-cache \
    tzdata \
    icu-libs \
    && rm -rf /var/cache/apk/*

WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=dotnet-build /app/publish ./
COPY --from=frontend-build /src/frontend/dist/admin ./wwwroot/admin
COPY .env.example ./.env.example

ENTRYPOINT ["dotnet", "OpenCodex.Api.dll"]
