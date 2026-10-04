FROM node:24-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY angular.json tsconfig.json ./
COPY projects/ projects/
RUN npx ng build feed-ops --configuration production

FROM nginx:1.29-alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY deploy/40-feedops-config.sh /docker-entrypoint.d/40-feedops-config.sh
COPY --from=build /src/dist/feed-ops/browser /usr/share/nginx/html
# A restrictive umask on the build machine (e.g. 077) would otherwise leave copied assets unreadable to nginx.
RUN chmod -R a+rX /usr/share/nginx/html
EXPOSE 80
