# Camoufox Integration Design

Replace the current Chromium + JavaScript stealth script setup in `PlaywrightWebBrowser` with Camoufox, an anti-detect browser built on Firefox that applies fingerprint spoofing at the C++ level inside SpiderMonkey.

## Motivation

The current anti-detection strategy relies on a ~150-line JavaScript stealth script injected at page load plus Chrome launch flags. This approach is fundamentally fragile because:

- Anti-bot systems can detect JS-level property overrides via property descriptor inspection, `Function.prototype.toString()` analysis, and timing attacks.
- The stealth script must be manually updated as detection techniques evolve.
- Chrome's automation indicators keep growing with each release.

Camoufox solves this by spoofing fingerprints at the browser engine's C++ level. When a site calls `navigator.userAgent` or any fingerprinted property, SpiderMonkey returns the spoofed value natively — no JS proxy objects, no race conditions, no detectable overrides.

## Architecture

### Sidecar Container

Camoufox runs as a dedicated Docker sidecar container using `camoufox-js` (Apify's Node.js port). It exposes a WebSocket endpoint that .NET Playwright connects to via `Firefox.ConnectAsync()`.

```
MCP Tool -> PlaywrightWebBrowser -> Firefox.ConnectAsync("ws://camoufox:9377/browser") -> Camoufox container
```

The `IWebBrowser` interface and all downstream code (Domain tools, MCP tools, HTML processing) remain untouched — only the browser initialization changes.

### Camoufox Container

**Dockerfile** (`DockerCompose/camoufox/Dockerfile`):

```dockerfile
FROM node:22-slim
RUN npm install -g camoufox-js && npx camoufox-js fetch
EXPOSE 9377
CMD ["npx", "camoufox-js", "server", "--port", "9377", "--ws-path", "browser"]
```

Notes:
- Single server instance = single browser instance = single fingerprint. For fingerprint rotation, restart the server or run multiple instances. Single instance is sufficient for the initial implementation.
- Consider `headless: "virtual"` mode (Xvfb) for harder-to-detect headless browsing if standard headless gets flagged.

### docker-compose.yml Additions

```yaml
camoufox:
  build: ./camoufox
  ports:
    - "9377:9377"
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:9377/json"]
    interval: 10s
    timeout: 5s
    retries: 3
  restart: unless-stopped

mcp-websearch:
  depends_on:
    camoufox:
      condition: service_healthy
  environment:
    - CAMOUFOX__WSENDPOINT=ws://camoufox:9377/browser
```

## PlaywrightWebBrowser Changes

### Removed

- **Stealth script** (~lines 29-182): The entire JS injection that patches `navigator.webdriver`, `navigator.plugins`, `window.chrome`, WebGL vendor/renderer, `userAgentData`, media codecs, and outer dimensions. Camoufox handles all of this natively.
- **Chrome launch arguments**: 14 flags including `--disable-blink-features=AutomationControlled`, `--no-sandbox`, `--disable-gpu`, etc.
- **Chrome-specific headers**: `sec-ch-ua`, `sec-ch-ua-mobile`, `sec-ch-ua-platform` — these are Chrome Client Hints that Firefox does not use.
- **Chrome User-Agent string**: Camoufox generates a realistic Firefox User-Agent via BrowserForge.
- **`Chromium.LaunchAsync()` / CDP connection path**: Replaced entirely.

### Added

- **WebSocket endpoint configuration**: Read from `CAMOUFOX__WSENDPOINT` environment variable.
- **Firefox connection**: `playwright.Firefox.ConnectAsync(wsEndpoint)` to connect to the Camoufox sidecar.
- **Connection retry**: Retry logic on initial connection since the sidecar may take a few seconds to become ready.

### Unchanged

- `IWebBrowser` interface — no contract changes.
- `BrowserSessionManager` — session tracking is browser-agnostic.
- `ModalDismisser` — CSS selectors and click logic work on any browser.
- `CapSolverClient` / CAPTCHA handling — cookie-based, browser-agnostic.
- `HtmlProcessor`, `HtmlInspector`, `HtmlConverter` — operate on HTML strings.
- All Domain tools (`WebBrowseTool`, `WebClickTool`, `WebInspectTool`) and MCP tools.
- Navigation logic, click handling, inspect modes.

### McpServerWebSearch Dockerfile

The `playwright-base` stage that installs Chromium via `npx playwright install chromium` can be removed entirely, significantly reducing the image size. The .NET app only needs the Playwright .NET package (which provides the client API) — the actual browser runs in the Camoufox sidecar.

## Testing

- Update `PlaywrightWebBrowserFixture` to connect to a Camoufox container instead of `browserless/chrome`.
- Existing integration tests should pass with minimal changes since they use the `IWebBrowser` abstraction.
- Add a smoke test that verifies the Camoufox connection and a basic navigation.
- Manual verification against bot detection test sites (`bot.sannysoft.com`, `browserleaks.com`) to confirm fingerprint quality.

## Risks

1. **Firefox vs Chrome rendering differences**: Some CSS selectors in `ModalDismisser` patterns might behave slightly differently. Low risk — standard selectors are used.
2. **DataDome CAPTCHA solver**: Currently works by setting cookies and reloading. Should work on Firefox but needs verification since CapSolver's `DataDomeSliderTask` may assume Chrome internals.
3. **Playwright Firefox API gaps**: A few Playwright APIs behave differently on Firefox (e.g., `page.route()` for network interception). The current code doesn't use these advanced APIs — low risk.
4. **Docker image size**: Camoufox bundles a full custom Firefox (~300-400MB). Acceptable since it replaces the Chromium install in `mcp-websearch` (similar size).
5. **Cloudflare in Docker**: Known issue where Cloudflare can detect Camoufox running in Docker in some configurations. May need `headless: "virtual"` mode or additional tuning.

## Rollback Plan

Keep the current Chromium Dockerfile and stealth script accessible in git history. If Camoufox proves unreliable, reverting is a single commit.
