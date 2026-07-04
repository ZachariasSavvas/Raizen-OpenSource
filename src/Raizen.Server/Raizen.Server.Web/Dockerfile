FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5002

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Raizen.Shared/Raizen.Shared.csproj", "src/Raizen.Shared/"]
COPY ["src/Raizen.Server/Raizen.Server.Core/Raizen.Server.Core.csproj", "src/Raizen.Server/Raizen.Server.Core/"]
COPY ["src/Raizen.Server/Raizen.Server.Web/Raizen.Server.Web.csproj", "src/Raizen.Server/Raizen.Server.Web/"]
RUN dotnet restore "src/Raizen.Server/Raizen.Server.Web/Raizen.Server.Web.csproj"

COPY . .
WORKDIR "/src/src/Raizen.Server/Raizen.Server.Web"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Raizen.Server.Web.dll"]
