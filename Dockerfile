FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Platform.slnx ./
COPY API/API.csproj API/
COPY Client/Client.csproj Client/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore API/API.csproj
COPY . .
# The API references the Client project, so the Blazor WASM app (wwwroot/_framework)
# is published into the API output and served same-origin.
RUN dotnet publish API/API.csproj -c Release -o /api-out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /api-out .
ENV ASPNETCORE_URLS=http://+:80 \
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false \
    DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 80
ENTRYPOINT ["dotnet", "API.dll"]
