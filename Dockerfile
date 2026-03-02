# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine@sha256:ebdd46c1a72c10bb992c276e5e6afc6e245757bd1a295d7f0870b266e11c306e AS build
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
FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine@sha256:90dd042888f6429c3711726c43556ff584288977221b91064d851931cffadbda AS final

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

