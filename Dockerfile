FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoReparos.OS.Api/AutoReparos.OS.Api.csproj AutoReparos.OS.Api/
COPY AutoReparos.OS.Application/AutoReparos.OS.Application.csproj AutoReparos.OS.Application/
COPY AutoReparos.OS.Domain/AutoReparos.OS.Domain.csproj AutoReparos.OS.Domain/
COPY AutoReparos.OS.Infrastructure/AutoReparos.OS.Infrastructure.csproj AutoReparos.OS.Infrastructure/
RUN dotnet restore AutoReparos.OS.Api/AutoReparos.OS.Api.csproj
COPY . .
RUN dotnet publish AutoReparos.OS.Api/AutoReparos.OS.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# Usuário sem privilégios que a imagem base do .NET já cria (APP_UID=1654).
USER $APP_UID
ENTRYPOINT ["dotnet", "AutoReparos.OS.Api.dll"]
