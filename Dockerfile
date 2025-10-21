FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 80
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_gcServer=true
ENV DOTNET_ThreadPoolMinThreads=100

HEALTHCHECK --interval=30s --timeout=5s CMD curl -f http://localhost/ping || exit 1

ENTRYPOINT ["dotnet", "AuthService.dll"]
