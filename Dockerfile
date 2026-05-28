# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG APP_VERSION

WORKDIR /src

COPY DumbMcpMultiplexer.slnx ./
COPY DumbMcpMultiplexer/DumbMcpMultiplexer.csproj DumbMcpMultiplexer/
COPY DumbMcpMultiplexer.Client/DumbMcpMultiplexer.Client.csproj DumbMcpMultiplexer.Client/

RUN dotnet restore

COPY . .

# Download Tailwind CSS standalone CLI for the build
ARG TARGETARCH
RUN TAILWIND_ARCH=$(case "$TARGETARCH" in arm64) echo "arm64" ;; *) echo "x64" ;; esac) && \
    wget -qO /usr/local/bin/tailwindcss "https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-linux-${TAILWIND_ARCH}" && \
    chmod +x /usr/local/bin/tailwindcss

RUN if [ -n "$APP_VERSION" ]; then \
      dotnet publish DumbMcpMultiplexer/DumbMcpMultiplexer.csproj -c Release -o /app/publish /p:Version=$APP_VERSION /p:TailwindCli=/usr/local/bin/tailwindcss; \
    else \
      dotnet publish DumbMcpMultiplexer/DumbMcpMultiplexer.csproj -c Release -o /app/publish /p:TailwindCli=/usr/local/bin/tailwindcss; \
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
