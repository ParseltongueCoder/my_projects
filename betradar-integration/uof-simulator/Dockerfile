FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/UofSim.Core/UofSim.Core.csproj src/UofSim.Core/
COPY src/UofSim.Host/UofSim.Host.csproj src/UofSim.Host/
RUN dotnet restore src/UofSim.Host/UofSim.Host.csproj
COPY src/ src/
RUN dotnet publish src/UofSim.Host/UofSim.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "UofSim.Host.dll"]
