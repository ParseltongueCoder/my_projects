FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Which service to publish: Uof.Adapter (default), Admin.Api or Bo.Api.
ARG PROJECT=Uof.Adapter
WORKDIR /src
COPY Directory.Build.props ./
COPY db/ db/
COPY src/Platform.Canonical/Platform.Canonical.csproj src/Platform.Canonical/
COPY src/Bo.Core/Bo.Core.csproj src/Bo.Core/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore && echo "${PROJECT}" > /app/.entrypoint

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8081 8082 8083
ENTRYPOINT ["sh", "-c", "exec dotnet $(cat /app/.entrypoint).dll"]
