# syntax=docker/dockerfile:1
# ElevenX Warehouse — .NET 10 Blazor Server (Interactive Server)
# Build context = repo root. ใช้กับ Railway/Render/Fly หรือ docker build เองก็ได้

# ---------- Stage 1: build Tailwind CSS ----------
# Tailwind v4 สแกน class จาก .razor/.cs (ดู @source ใน app.tailwind.css) จึงต้องมี source ด้วย
FROM node:24-slim AS css
WORKDIR /src
COPY elevenx-warehouse/package.json elevenx-warehouse/package-lock.json ./
RUN npm ci
COPY elevenx-warehouse/app.tailwind.css ./
COPY elevenx-warehouse/Components ./Components
COPY elevenx-warehouse/Program.cs ./Program.cs
RUN mkdir -p wwwroot && npm run build:css

# ---------- Stage 2: restore + publish .NET ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# restore แยกชั้น เพื่อใช้ layer cache ตอน source เปลี่ยนแต่ dependency ไม่เปลี่ยน
COPY elevenx-warehouse/ElevenX.Warehouse.csproj ./elevenx-warehouse/
RUN dotnet restore ./elevenx-warehouse/ElevenX.Warehouse.csproj
COPY elevenx-warehouse/ ./elevenx-warehouse/
# วาง CSS ที่ build แล้วก่อน publish เพื่อให้ MapStaticAssets ฝัง fingerprint ให้
COPY --from=css /src/wwwroot/app.css ./elevenx-warehouse/wwwroot/app.css
RUN dotnet publish ./elevenx-warehouse/ElevenX.Warehouse.csproj \
    -c Release -o /app /p:UseAppHost=false

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# libgssapi-krb5-2: Npgsql probe หา GSSAPI ตอนเปิด connection — ใส่ไว้กัน error log รก
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
ENV ASPNETCORE_ENVIRONMENT=Production
# พอร์ตเริ่มต้นในคอนเทนเนอร์ของ .NET คือ 8080 — Railway จะ override ด้วย env PORT เอง
EXPOSE 8080
ENTRYPOINT ["dotnet", "ElevenX.Warehouse.dll"]
