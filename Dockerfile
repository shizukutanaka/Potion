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

# Create non-root user for security
RUN groupadd -r potion && useradd -r -g potion potion
USER potion

# Service listens on Kestrel HTTP :5000 by default (HTTPS in Production via
# the Kestrel cert config — see appsettings.Production.json)
EXPOSE 5000

# Start the application
ENTRYPOINT ["dotnet", "Potion.Service.dll"]
