FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY db/ db/
COPY src/Platform.Canonical/Platform.Canonical.csproj src/Platform.Canonical/
COPY src/Uof.Adapter/Uof.Adapter.csproj src/Uof.Adapter/
RUN dotnet restore src/Uof.Adapter/Uof.Adapter.csproj
COPY src/ src/
RUN dotnet publish src/Uof.Adapter/Uof.Adapter.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Uof.Adapter.dll"]
