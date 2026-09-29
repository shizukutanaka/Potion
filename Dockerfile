# Potion Self-Healing Service Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Repo-root build props enable lock-file restore; without them the
# container build would silently ignore packages.lock.json pins.
COPY ["Directory.Build.props", "./"]

# Copy the project file and its lock file first so the NuGet restore layer
# stays cached unless package references themselves change. The lock file
# must arrive before restore: locked mode fails when it is absent.
COPY ["src/Potion.Service/Potion.Service.csproj", "Potion.Service/"]
COPY ["src/Potion.Service/packages.lock.json", "Potion.Service/"]

# Container builds are CI builds: fail on a lock-file mismatch instead
# of silently rewriting it (see RestoreLockedMode in Directory.Build.props).
ENV ContinuousIntegrationBuild=true

# Restore dependencies
WORKDIR "/src/Potion.Service"
RUN dotnet restore "Potion.Service.csproj"

# Copy the remaining sources (.dockerignore excludes bin/obj)
WORKDIR /src
COPY ["src/Potion.Service/", "Potion.Service/"]

# Build the application
WORKDIR "/src/Potion.Service"
RUN dotnet build "Potion.Service.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "Potion.Service.csproj" -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .

# Create non-root user for security. Give it a writable HOME so
# ServicePaths' LocalApplicationData fallback has somewhere to go —
# without it the service can fail lazily when it first writes state.
RUN groupadd -r -g 101 potion && useradd -r -u 101 -g potion potion \
    && mkdir -p /home/potion /app/logs \
    && chown -R potion:potion /home/potion /app/logs
ENV HOME=/home/potion
USER potion

# Default the image to its Container environment so a bare `docker run`
# binds Kestrel to +:80 via appsettings.Container.json — without it the
# image falls back to Production config (localhost binding + a Windows
# cert path), leaving published ports unreachable or the boot crashing.
# Compose and k8s set this same value; `-e` can still override it.
ENV ASPNETCORE_ENVIRONMENT=Container

EXPOSE 80

# Start the application
ENTRYPOINT ["dotnet", "Potion.Service.dll"]
