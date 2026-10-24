FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5001
ENV ASPNETCORE_URLS=http://+:5001

# Задача 68: Claude Code CLI для ИИ-агентов (закреплённая версия — флаги раннера сверены с её `claude --help`).
# Ставится npm в отдельной стадии; в финальный образ копируются только бинарь Node.js LTS и установленный пакет (npm и его кэш — нет).
# Пакет 2.x при установке кладёт нативный бинарь платформы в bin/claude.exe — каталог платформенных пакетов не копируем.
FROM node:22.20.0-bookworm-slim AS claude-cli
ARG CLAUDE_CODE_VERSION=2.1.296
RUN npm install -g --no-audit --no-fund "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && rm -rf /usr/local/lib/node_modules/@anthropic-ai/claude-code/node_modules \
    && /usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe --version

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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
COPY --from=claude-cli /usr/local/bin/node /usr/local/bin/node
COPY --from=claude-cli /usr/local/lib/node_modules/@anthropic-ai/claude-code /usr/local/lib/node_modules/@anthropic-ai/claude-code
RUN ln -s /usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe /usr/local/bin/claude \
    && claude --version \
    && node --version
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bonds.Api.dll"]
