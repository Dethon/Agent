FROM mcr.microsoft.com/playwright:v1.59.0-noble

WORKDIR /app

RUN npm install camoufox-js@0.9.3 playwright-core@1.59.0 && npx camoufox-js fetch

COPY server.js .

EXPOSE 9377

CMD ["node", "server.js"]
