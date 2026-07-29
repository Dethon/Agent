---
paths:
  - "McpServerWebSearch/**"
  - "Domain/Tools/Web/**"
  - "Domain/Prompts/WebBrowsingPrompt.cs"
  - "Domain/Contracts/IWebBrowser.cs"
  - "Infrastructure/Clients/Browser/**"
  - "DockerCompose/camoufox/**"
---

# Web Browsing Architecture

McpServerWebSearch exposes `web_browse` (navigate + extract), `web_snapshot` (accessibility tree with interactive element refs) and `web_action` (click/type/fill/select by ref). The backend is `PlaywrightWebBrowser` over a WebSocket to Camoufox. `AccessibilitySnapshotService` injects JS to traverse the DOM, infer ARIA roles and assign refs (`e-1`, `e-2`, …); `BrowserSessionManager` keeps pages alive per session with cookie persistence; `ModalDismisser` auto-closes cookie banners, newsletters and age gates.

## Camoufox

The `camoufox` service is an anti-detect Firefox for scraping, reached at `ws://camoufox:9377/browser`; config in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

**Bumping `Microsoft.Playwright` is a two-sided change.** The connect handshake demands an exact client/server minor match — a mismatch fails hard with HTTP 428 `Playwright version mismatch`. `DockerCompose/camoufox/Dockerfile` must move in lockstep: both the `mcr.microsoft.com/playwright:vX.Y.0-noble` base and `playwright-core@X.Y.0`. Camoufox's bundled Firefox carries an older juggler protocol, so a newer playwright-core can send fields it rejects; `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653) — their own Python library just pins `playwright<1.61`). Both `patch-*.js` scripts are anchor-checked and **exit 1 when their anchor disappears**, so a bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` after any bump.
