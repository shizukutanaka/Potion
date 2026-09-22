# Potion Self-Healing Service Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the whole project directory (.dockerignore excludes bin/obj)
COPY ["src/Potion.Service/", "Potion.Service/"]

# Restore dependencies
WORKDIR "/src/Potion.Service"
RUN dotnet restore "Potion.Service.csproj"

# Build the application
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
RUN groupadd -r potion && useradd -r -g potion potion \
    && mkdir -p /home/potion /app/logs \
    && chown -R potion:potion /home/potion /app/logs
ENV HOME=/home/potion
USER potion

# Compose/k8s override the Kestrel endpoint to :80; :5000 is the local-dev
# default (HTTPS in Production via the cert config — see
# appsettings.Production.json)
EXPOSE 80
EXPOSE 5000

# Start the application
ENTRYPOINT ["dotnet", "Potion.Service.dll"]
