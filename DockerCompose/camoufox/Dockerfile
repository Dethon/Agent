FROM mcr.microsoft.com/playwright:v1.60.0-noble

WORKDIR /app

RUN npm install camoufox-js@0.9.3 playwright-core@1.60.0 && npx camoufox-js fetch

# Guard a playwright-core crash on locationless uncaught page errors (see patch-playwright.js).
COPY patch-playwright.js .
RUN node patch-playwright.js

COPY server.js .

EXPOSE 9377

CMD ["node", "server.js"]
