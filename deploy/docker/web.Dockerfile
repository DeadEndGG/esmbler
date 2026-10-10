# syntax=docker/dockerfile:1.7
# Optional build secret "extra_ca": an extra CA bundle for builds behind a TLS-intercepting proxy.
FROM node:24-alpine AS build
WORKDIR /web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm,sharing=locked --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca; fi; \
    npm ci --no-audit --no-fund
COPY apps/web/ ./
COPY packages/Esmbler.Expressions/conformance/ /packages/Esmbler.Expressions/conformance/
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca; fi; \
    npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
COPY deploy/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist/web/browser /usr/share/nginx/html
EXPOSE 8080
