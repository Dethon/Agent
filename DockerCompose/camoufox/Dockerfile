FROM node:22-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libgtk-3-0 \
    libx11-xcb1 \
    libasound2 \
    libdbus-glib-1-2 \
    libgbm1 \
    libnss3 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    libxkbcommon0 \
    fonts-liberation \
    xvfb \
    curl \
    python3 \
    make \
    g++ \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

RUN npm install camoufox-js@0.9.3 playwright-core@1.58.2 && npx camoufox-js fetch

COPY server.js .

EXPOSE 9377

CMD ["node", "server.js"]
