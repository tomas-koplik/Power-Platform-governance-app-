FROM node:22-alpine AS build
WORKDIR /src
COPY src/Ppgsm.Web/package*.json ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY src/Ppgsm.Web/ ./
ENV VITE_DATA_ADAPTER=live
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY src/Ppgsm.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --chown=101:101 --chmod=755 src/Ppgsm.Web/docker-entrypoint.d/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
COPY --from=build --chown=101:101 /src/dist /usr/share/nginx/html
EXPOSE 8080