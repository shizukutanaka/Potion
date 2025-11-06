# Potion Self-Healing Service Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["src/Potion.Service/Potion.Service.csproj", "Potion.Service/"]
COPY ["src/Potion.Service/Hubs/", "Potion.Service/Hubs/"]
COPY ["src/Potion.Service/Controllers/", "Potion.Service/Controllers/"]
COPY ["src/Potion.Service/Infrastructure/", "Potion.Service/Infrastructure/"]
COPY ["src/Potion.Service/Options/", "Potion.Service/Options/"]
COPY ["src/Potion.Service/Resources/", "Potion.Service/Resources/"]
COPY ["src/Potion.Service/Program.cs", "Potion.Service/"]
COPY ["src/Potion.Service/Startup.cs", "Potion.Service/"]
COPY ["src/Potion.Service/appsettings*.json", "Potion.Service/"]

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

# Create non-root user for security
RUN groupadd -r potion && useradd -r -g potion potion
USER potion

# Expose ports
EXPOSE 80
EXPOSE 443

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD curl -f http://localhost:80/api/health/liveness || exit 1

# Start the application
ENTRYPOINT ["dotnet", "Potion.Service.dll"]
