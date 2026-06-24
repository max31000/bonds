FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5001
ENV ASPNETCORE_URLS=http://+:5001

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
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bonds.Api.dll"]
