# Базовые образы закреплены по digest (ревью T-68 MINOR 6); тег оставлен для читаемости. Обновление: `docker buildx imagetools inspect <образ:тег>` → новый digest.
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:74a1d7c07211a7e8a2e35ff3c70ed8e4e1d6451a1ff7cf7bdf526f9eafa23023 AS base
WORKDIR /app
EXPOSE 5001
ENV ASPNETCORE_URLS=http://+:5001

# Задача 68: Claude Code CLI для ИИ-агентов (закреплённая версия — флаги раннера сверены с её `claude --help`).
# npm ставит пакет в отдельной стадии; пакет 2.x кладёт НАТИВНЫЙ бинарь платформы (ELF, зависит только от glibc: librt/libc/libpthread/libdl/libm)
# в bin/claude.exe — в финальный образ копируется только он: ни Node.js, ни npm, ни node_modules.
# Сборка ПАДАЕТ, если в этой версии CLI нет флагов, на которые опирается раннер (ClaudeCodeCli.BuildArguments) — иначе CLI тихо проигнорировал бы
# ограничения инструментов/разрешений (ревью M2).
FROM node:22.20.0-bookworm-slim@sha256:b21fe589dfbe5cc39365d0544b9be3f1f33f55f3c86c87a76ff65a02f8f5848e AS claude-cli
ARG CLAUDE_CODE_VERSION=2.1.296
RUN npm install -g --no-audit --no-fund "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && cp /usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe /claude \
    && /claude --version \
    && /claude --help > /tmp/claude-help.txt \
    && for flag in --permission-prompts --strict-mcp-config --allowedTools --no-session-persistence --mcp-config --tools --output-format; do \
         grep -q -- "$flag" /tmp/claude-help.txt || { echo "claude ${CLAUDE_CODE_VERSION}: required flag $flag is missing from --help" >&2; exit 1; }; \
       done

FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:738f72aff955d5c5690e06b6fc8ecd1b90ead564099a257ac528ff720b8da85f AS build
WORKDIR /src
COPY ["src/Bonds.Api/Bonds.Api.csproj", "src/Bonds.Api/"]
COPY ["src/Bonds.Core/Bonds.Core.csproj", "src/Bonds.Core/"]
COPY ["src/Bonds.Infrastructure/Bonds.Infrastructure.csproj", "src/Bonds.Infrastructure/"]
RUN dotnet restore "src/Bonds.Api/Bonds.Api.csproj"
COPY . .
WORKDIR "/src/src/Bonds.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
# Ревью M1: CLI агента работает под непривилегированным `agent` (uid/gid 10001, без home, nologin) — раннер запускает его через
# /usr/bin/setpriv (util-linux, уже есть в aspnet:8.0). API пока остаётся root. Каталоги запусков — /var/lib/bonds-agent (root, 0711; каталог запуска
# 0700 владельца agent создаёт API).
COPY --from=claude-cli /claude /usr/local/bin/claude
RUN groupadd --system --gid 10001 agent \
    && useradd --system --uid 10001 --gid 10001 --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin agent \
    && install -d -o root -g root -m 0711 /var/lib/bonds-agent \
    && test -x /usr/bin/setpriv \
    && claude --version \
    && /usr/bin/setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all -- /usr/local/bin/claude --version
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bonds.Api.dll"]
