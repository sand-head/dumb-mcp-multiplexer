# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG APP_VERSION
ARG TARGETARCH

WORKDIR /src

COPY DumbMcpMultiplexer.slnx ./
COPY DumbMcpMultiplexer/DumbMcpMultiplexer.csproj DumbMcpMultiplexer/
COPY DumbMcpMultiplexer.Client/DumbMcpMultiplexer.Client.csproj DumbMcpMultiplexer.Client/

RUN dotnet restore

# Install Node.js 22 (required for Tailwind v4 build)
RUN apt-get update \
	&& apt-get install -y ca-certificates curl \
	&& curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
	&& apt-get install -y nodejs \
	&& rm -rf /var/lib/apt/lists/*

# Copy npm manifest files for Tailwind install (cache-friendly)
COPY package.json package-lock.json ./
RUN npm ci

COPY . .

RUN DOTNET_RID=$(case "$TARGETARCH" in arm64) echo "linux-arm64" ;; *) echo "linux-x64" ;; esac) && \
    if [ -n "$APP_VERSION" ]; then \
      dotnet publish DumbMcpMultiplexer/DumbMcpMultiplexer.csproj -c Release -r $DOTNET_RID -o /app/publish /p:Version=$APP_VERSION; \
    else \
      dotnet publish DumbMcpMultiplexer/DumbMcpMultiplexer.csproj -c Release -r $DOTNET_RID -o /app/publish; \
    fi

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

RUN mkdir -p /data

ENV ASPNETCORE_URLS=http://+:7899
ENV ConnectionStrings__DefaultConnection="Data Source=/data/proxy.db"

EXPOSE 7899

ENTRYPOINT ["dotnet", "DumbMcpMultiplexer.dll"]
