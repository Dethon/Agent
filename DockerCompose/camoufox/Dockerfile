FROM mcr.microsoft.com/playwright:v1.61.0-noble

WORKDIR /app

RUN npm install camoufox-js@0.9.3 playwright-core@1.61.0 && npx camoufox-js fetch

# Guard a playwright-core crash on locationless uncaught page errors (see patch-playwright.js).
COPY patch-playwright.js .
RUN node patch-playwright.js

# Playwright's client/server version check is an exact minor match, so playwright-core here must
# track Microsoft.Playwright in the .NET projects. 1.61 sends juggler viewport fields Camoufox's
# Firefox build does not describe (see patch-viewport.js).
COPY patch-viewport.js .
RUN node patch-viewport.js

COPY server.js .

EXPOSE 9377

CMD ["node", "server.js"]
