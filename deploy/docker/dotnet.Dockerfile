# syntax=docker/dockerfile:1.7
# Builds one .NET host (API, MCP, or worker) selected by APP. Separate images per host (execution brief section 2).
# Optional build secret "extra_ca": an extra CA bundle for builds behind a TLS-intercepting proxy.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG APP
WORKDIR /src
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then cp /run/secrets/extra_ca /usr/local/share/ca-certificates/extra.crt && update-ca-certificates; fi
COPY global.json Directory.Build.props Directory.Packages.props Esmbler.slnx ./
COPY packages/ packages/
COPY modules/ modules/
COPY apps/Esmbler.Api/ apps/Esmbler.Api/
COPY apps/Esmbler.Mcp/ apps/Esmbler.Mcp/
COPY apps/Esmbler.Worker/ apps/Esmbler.Worker/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish apps/${APP}/${APP}.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG APP
ENV APP_DLL=${APP}.dll
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
