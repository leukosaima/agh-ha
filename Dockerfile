# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY AdGuardHomeHA.csproj .
RUN dotnet restore "AdGuardHomeHA.csproj"

# Copy source code and publish with optimizations
COPY . .
RUN dotnet publish "AdGuardHomeHA.csproj" \
    -c Release \
    --no-restore \
    -o /app/publish

# Runtime stage - ASP.NET Core runtime image (needed for web framework)
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final

# Install dependencies and create non-root user
# iputils-ping installs a proper ping binary with setuid root
RUN apk add --no-cache icu-libs iputils \
    && addgroup -g 1001 -S appuser \
    && adduser -S -D -H -u 1001 -h /app -s /sbin/nologin -G appuser appuser

WORKDIR /app

# Copy published app
COPY --from=build /app/publish .

# Create directory for configuration and set ownership
RUN mkdir -p /app/config && chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD pgrep -f AdGuardHomeHA || exit 1

# Environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_EnableDiagnostics=0

# Expose webhook port for Gatus webhooks
EXPOSE 8080

# Add capability for ping (ICMP) functionality
# Note: This requires --cap-add=NET_RAW when running the container

# Run the application
ENTRYPOINT ["dotnet", "AdGuardHomeHA.dll"]
