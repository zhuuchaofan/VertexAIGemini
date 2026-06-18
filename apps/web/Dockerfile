FROM node:24-alpine AS runtime

WORKDIR /app

COPY package.json ./
COPY server.mjs ./
COPY public ./public

ENV NODE_ENV=production
ENV HOST=0.0.0.0
ENV PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD wget -qO- "http://127.0.0.1:${PORT:-8080}/" >/dev/null || exit 1

CMD ["node", "server.mjs"]
