FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Platform.slnx ./
COPY API/API.csproj API/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore API/API.csproj
COPY . .
RUN dotnet publish API/API.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "API.dll"]
