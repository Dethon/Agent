FROM node:22-slim
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
RUN npm install -g camoufox-js && npx camoufox-js fetch
EXPOSE 9377
CMD ["npx", "camoufox-js", "server", "--port", "9377", "--ws-path", "browser"]
