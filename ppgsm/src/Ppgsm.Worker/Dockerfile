FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore Ppgsm.sln --locked-mode || dotnet restore Ppgsm.sln
RUN dotnet publish src/Ppgsm.Worker/Ppgsm.Worker.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
RUN addgroup -S ppgsm && adduser -S ppgsm -G ppgsm
COPY --from=build --chown=ppgsm:ppgsm /app .
USER ppgsm
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Ppgsm.Worker.dll"]
