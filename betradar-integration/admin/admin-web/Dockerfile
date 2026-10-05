FROM node:24-alpine AS build
# Which app of the workspace: feed-ops (default) or backoffice.
ARG APP=feed-ops
WORKDIR /src
COPY package.json package-lock.json ./
# Retry flaky downloads and keep the npm cache between builds (both web images and repeated builds reuse it).
RUN --mount=type=cache,target=/root/.npm \
    npm ci --no-audit --no-fund --prefer-offline \
      --fetch-retries=5 --fetch-retry-mintimeout=20000 --fetch-retry-maxtimeout=120000
COPY angular.json tsconfig.json ./
COPY projects/ projects/
RUN npx ng build ${APP} --configuration production && mv dist/${APP}/browser /out

FROM nginx:1.29-alpine
# nginx's entrypoint renders templates with envsubst (only defined variables: $uri etc. stay untouched).
ENV API_UPSTREAM=admin-api:8082
COPY deploy/nginx.conf /etc/nginx/templates/default.conf.template
COPY deploy/40-feedops-config.sh /docker-entrypoint.d/40-feedops-config.sh
COPY --from=build /out /usr/share/nginx/html
# A restrictive umask on the build machine (e.g. 077) would otherwise leave copied assets unreadable to nginx.
RUN chmod -R a+rX /usr/share/nginx/html
EXPOSE 80
