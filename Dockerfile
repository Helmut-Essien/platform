FROM mcr.microsoft.com/dotnet/sdk:9.0 AS client-build
WORKDIR /src
COPY Platform.slnx ./
COPY Client/Client.csproj Client/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore Client/Client.csproj
COPY . .
RUN echo '{}' > Client/wwwroot/appsettings.json \
    && dotnet publish Client/Client.csproj -c Release -o /client-out

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api-build
WORKDIR /src
COPY Platform.slnx ./
COPY API/API.csproj API/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore API/API.csproj
COPY . .
RUN dotnet publish API/API.csproj -c Release -o /api-out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=api-build /api-out .
COPY --from=client-build /client-out/wwwroot wwwroot/
ENV ASPNETCORE_URLS=http://+:80 \
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false \
    DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 80
ENTRYPOINT ["dotnet", "API.dll"]
