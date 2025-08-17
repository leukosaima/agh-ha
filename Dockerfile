# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY AdGuardHomeHA.csproj .
RUN dotnet restore "AdGuardHomeHA.csproj"

# Copy source code and build
COPY . .
RUN dotnet build "AdGuardHomeHA.csproj" -c Release -o /app/build --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish "AdGuardHomeHA.csproj" -c Release -o /app/publish --no-build --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# Create non-root user for security
RUN groupadd -r appuser && useradd -r -g appuser appuser

WORKDIR /app

# Copy published app
COPY --from=publish /app/publish .

# Create directory for configuration and set ownership
RUN mkdir -p /app/config && chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD ps aux | grep -q '[A]dGuardHomeHA' || exit 1

# Environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_EnableDiagnostics=0

# Expose no ports (this is an internal service)

# Run the application
ENTRYPOINT ["dotnet", "AdGuardHomeHA.dll"]
