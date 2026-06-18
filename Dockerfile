# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:fac7cce841f78faa4bca416fb4c636d1a129c09abd9b50e9b45664b95fd008a0 AS build
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

# Runtime stage - .NET runtime image (console application)
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:9c8ae7f4979a26a18ec854df74c74c39f31f50956a77fd518046d75dbeeb1370 AS final

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

# No ports needed - this is a background service

# Run the application
ENTRYPOINT ["dotnet", "AdGuardHomeHA.dll"]


