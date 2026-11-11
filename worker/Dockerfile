FROM public.ecr.aws/docker/library/node:22-alpine
WORKDIR /app
COPY worker/package.json worker/package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund
COPY worker/server.mjs ./
ENV EPISTEME_FETCH_PORT=8090 \
    EPISTEME_FETCH_MAX_BYTES=2097152 \
    EPISTEME_FETCH_TIMEOUT_MS=9000 \
    NODE_ENV=production
USER node
EXPOSE 8090
CMD ["node", "server.mjs"]
