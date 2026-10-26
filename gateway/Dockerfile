FROM golang:1.27-alpine AS build
WORKDIR /src
COPY gateway/ .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /gateway .
FROM alpine:3.23
WORKDIR /app
COPY --from=build /gateway /app/gateway
COPY web/ /app/web/
USER 65534:65534
EXPOSE 8080
ENTRYPOINT ["/app/gateway"]
