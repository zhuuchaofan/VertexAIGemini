# Build the Vite client (served under /app/). The legacy UI in public/ needs no build.
FROM node:24-alpine AS client-build

WORKDIR /build

COPY client/package.json client/package-lock.json ./
RUN npm ci

COPY client/ ./
RUN npm run build


FROM node:24-alpine AS runtime

WORKDIR /app

COPY package.json ./
COPY server.mjs ./
COPY public ./public
COPY --from=client-build /build/dist ./client/dist

ENV NODE_ENV=production
ENV HOST=0.0.0.0
ENV PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD wget -qO- "http://127.0.0.1:${PORT:-8080}/" >/dev/null || exit 1

CMD ["node", "server.mjs"]
