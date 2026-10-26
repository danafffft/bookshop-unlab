FROM golang:1.27-alpine AS go-build
WORKDIR /src
COPY gateway/ .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /gateway .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY api/ api/
COPY db/ db/
RUN dotnet publish api/CherryBooks.csproj -c Release -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libpq5 ca-certificates bash && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=go-build /gateway /app/gateway
COPY --from=api-build /publish /app/api/
COPY web/ /app/web/
COPY deploy/render/start.sh /app/start.sh
RUN sed -i 's/\r$//' /app/start.sh && chmod 755 /app/start.sh
ENV WEB_ROOT=/app/web API_URL=http://127.0.0.1:5080 ASPNETCORE_URLS=http://127.0.0.1:5080 COOKIE_SECURE=true
USER $APP_UID
EXPOSE 10000
ENTRYPOINT ["/bin/bash", "/app/start.sh"]
