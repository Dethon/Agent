# WebChat Refresh — Plan A: Foundations & Visual Reskin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give WebChat the "Ember Study" visual identity (warm paper + warm espresso themes, self-hosted fonts, ember as the one action color, per-space tint plumbing, warm monograms, reduced-motion support) — without changing navigation structure. Shippable on its own.

**Architecture:** The app already themes entirely through CSS custom properties (`--accent`, `--bg-*`, `--text-*`, etc.) defined in `:root` and `[data-theme="dark"]`. Swapping those token *values* reskins every component that uses them, with zero structural edits. We add: self-hosted fonts (+ a service-worker precache fix), a separate `--space-accent` for the per-space tint (so it never overrides ember), a warm monogram palette, and a global `prefers-reduced-motion` guard.

**Tech Stack:** Blazor WebAssembly, CSS custom properties, `app.js` JS interop, xUnit + Shouldly (the only unit-testable parts here are the two pure-C# changes; CSS/asset/JS work is verified by build + visual QA + existing E2E). **There is no bUnit** — components are not unit-testable in this repo.

**Scope note:** This is Plan A of three. Plan B (cross-agent activity state) and Plan C (The Hearth navigation) follow as separate documents and depend on this one. This plan deliberately does **not** touch `.topic-sidebar`/`.topic-list`/`.topic-item` structure (Plan C rewrites it); those rules reskin automatically via the new token values.

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `WebChat.Client/wwwroot/fonts/*.woff2` | Create | Self-hosted Fraunces (variable), Hanken Grotesk (400/600), JetBrains Mono (400/500) |
| `WebChat.Client/wwwroot/css/app.css` | Modify | `@font-face` block; replace `:root`+`[data-theme="dark"]` token values; add reduced-motion guard |
| `WebChat.Client/wwwroot/index.html` | Modify | Remove Google Fonts `<link>`/preconnects; preload critical local faces |
| `WebChat.Client/wwwroot/service-worker.published.js` | Modify | Add `.woff2` to `offlineAssetsInclude` |
| `WebChat.Client/wwwroot/app.js` | Modify | Add `accentHelper.setVar` (sets `--space-accent`) |
| `WebChat.Client/Layout/MainLayout.razor` | Modify | Call `accentHelper.setVar` alongside the existing favicon update |
| `WebChat.Client/Helpers/AvatarHelper.cs` | Modify | Swap cold `_colors` for a warm Ember-Study ramp |
| `Domain/DTOs/WebChat/SpaceConfig.cs` | Modify | Warm `DefaultAccentColor` |
| `WebChat/appsettings.json` | Modify | Default space accent → warm |
| `Tests/Unit/Domain/DTOs/WebChat/SpaceConfigTests.cs` | Create | Pin the warm default + validity |
| `Tests/Unit/WebChat.Client/Helpers/AvatarHelperTests.cs` | Create | Determinism + warm palette membership |

---

### Task 1: Self-host the three typefaces

**Files:**
- Create: `WebChat.Client/wwwroot/fonts/fraunces.woff2` (variable, wght+opsz, Latin), `hanken-grotesk-400.woff2`, `hanken-grotesk-600.woff2`, `jetbrains-mono-400.woff2`, `jetbrains-mono-500.woff2`
- Modify: `WebChat.Client/wwwroot/css/app.css` (top of file), `WebChat.Client/wwwroot/index.html`

This is asset wiring — no unit test. Verified by build + visual check.

- [ ] **Step 1: Acquire the woff2 files**

Download Latin-subset `woff2` files (all three are OFL-licensed) from a font CDN bundler such as `https://gwfh.mranftl.com/fonts` (google-webfonts-helper) or Fontsource:
- **Fraunces** — the *variable* `woff2` (weight + optical-size axes), Latin subset → save as `fraunces.woff2`
- **Hanken Grotesk** — weights 400 and 600, Latin → `hanken-grotesk-400.woff2`, `hanken-grotesk-600.woff2`
- **JetBrains Mono** — weights 400 and 500, Latin → `jetbrains-mono-400.woff2`, `jetbrains-mono-500.woff2`

Place all five under `WebChat.Client/wwwroot/fonts/`.

- [ ] **Step 2: Add the `@font-face` block at the very top of `app.css`**

Insert before the `/* Theme Variables */` comment:

```css
/* ===================================
   Self-hosted fonts (Ember Study)
   =================================== */

@font-face {
    font-family: 'Fraunces';
    src: url('/fonts/fraunces.woff2') format('woff2');
    font-weight: 400 700;
    font-style: normal;
    font-display: swap;
}

@font-face {
    font-family: 'Hanken Grotesk';
    src: url('/fonts/hanken-grotesk-400.woff2') format('woff2');
    font-weight: 400;
    font-style: normal;
    font-display: swap;
}

@font-face {
    font-family: 'Hanken Grotesk';
    src: url('/fonts/hanken-grotesk-600.woff2') format('woff2');
    font-weight: 600;
    font-style: normal;
    font-display: swap;
}

@font-face {
    font-family: 'JetBrains Mono';
    src: url('/fonts/jetbrains-mono-400.woff2') format('woff2');
    font-weight: 400;
    font-style: normal;
    font-display: swap;
}

@font-face {
    font-family: 'JetBrains Mono';
    src: url('/fonts/jetbrains-mono-500.woff2') format('woff2');
    font-weight: 500;
    font-style: normal;
    font-display: swap;
}
```

- [ ] **Step 3: Point the base font stack at Hanken Grotesk**

In `app.css`, the `html, body` rule (currently line ~130) reads:

```css
    font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
```

Replace with:

```css
    font-family: 'Hanken Grotesk', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
```

(The existing `'JetBrains Mono', 'Fira Code', ...` monospace stacks already name JetBrains Mono — leave them; they now resolve to the self-hosted face.)

- [ ] **Step 4: Remove the Google Fonts CDN from `index.html` and preload the critical faces**

In `index.html`, delete these lines (the preconnects + the Inter/JetBrains stylesheet link):

```html
    <!-- Preconnect for performance -->
    <link href="https://fonts.googleapis.com" rel="preconnect"/>
    <link crossorigin href="https://fonts.gstatic.com" rel="preconnect"/>

    <!-- Inter font for modern typography -->
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap"
          rel="stylesheet"/>
```

In their place, preload the two faces needed for first paint (body text + brand mark):

```html
    <!-- Self-hosted fonts: preload critical faces for first paint -->
    <link rel="preload" href="/fonts/hanken-grotesk-400.woff2" as="font" type="font/woff2" crossorigin/>
    <link rel="preload" href="/fonts/fraunces.woff2" as="font" type="font/woff2" crossorigin/>
```

- [ ] **Step 5: Build and visually verify the fonts load**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

Then run the app (per CLAUDE.md compose command) and confirm in the browser devtools Network tab that `fonts/*.woff2` are served locally (no `fonts.googleapis.com` requests) and headings/body render in Fraunces/Hanken Grotesk.

- [ ] **Step 6: Commit**

```bash
git add WebChat.Client/wwwroot/fonts WebChat.Client/wwwroot/css/app.css WebChat.Client/wwwroot/index.html
git commit -m "feat(webchat): self-host Ember Study fonts; drop Google Fonts CDN"
```

---

### Task 2: Fix the service-worker font precache

**Files:**
- Modify: `WebChat.Client/wwwroot/service-worker.published.js:13`

Without this, self-hosted `.woff2` files are not cached for the installed PWA (the regex `/\.woff$/` does not match `.woff2`), so offline loads fall back to system fonts.

- [ ] **Step 1: Add `.woff2` to `offlineAssetsInclude`**

The line currently is:

```js
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
```

Change `/\.woff$/` to `/\.woff2?$/`:

```js
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
```

- [ ] **Step 2: Verify**

Run: `grep -n "woff" WebChat.Client/wwwroot/service-worker.published.js`
Expected: shows `/\.woff2?$/` (matches both `.woff` and `.woff2`). No unit test — `service-worker.published.js` only runs in a published build.

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/service-worker.published.js
git commit -m "fix(webchat): precache .woff2 fonts in the published service worker"
```

---

### Task 3: Warm default space accent

**Files:**
- Modify: `Domain/DTOs/WebChat/SpaceConfig.cs:7`, `WebChat/appsettings.json:3`
- Test: `Tests/Unit/Domain/DTOs/WebChat/SpaceConfigTests.cs` (create)

The current default `#e94560` (pink-red) is not warm. Default it to ember so out-of-box spaces harmonize. (Per-space overrides still apply; this only changes the no-override default + favicon tint.)

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/DTOs/WebChat/SpaceConfigTests.cs`:

```csharp
using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public sealed class SpaceConfigTests
{
    [Fact]
    public void DefaultAccentColor_IsWarmEmber()
    {
        SpaceConfig.DefaultAccentColor.ShouldBe("#e9601f");
    }

    [Fact]
    public void DefaultAccentColor_IsAValidHexColor()
    {
        SpaceConfig.IsValidHexColor(SpaceConfig.DefaultAccentColor).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpaceConfigTests"`
Expected: FAIL — `DefaultAccentColor_IsWarmEmber` reports `"#e94560"` should be `"#e9601f"`.

- [ ] **Step 3: Change the default**

In `Domain/DTOs/WebChat/SpaceConfig.cs`, change line 7:

```csharp
    public const string DefaultAccentColor = "#e9601f";
```

- [ ] **Step 4: Update the default-space config to match**

In `WebChat/appsettings.json:3`, change the `"default"` space's accent:

```json
    { "Slug": "default", "Name": "Main", "AccentColor": "#e9601f" },
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpaceConfigTests"`
Expected: PASS (both facts).

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/WebChat/SpaceConfig.cs WebChat/appsettings.json Tests/Unit/Domain/DTOs/WebChat/SpaceConfigTests.cs
git commit -m "feat(webchat): warm Ember default space accent"
```

---

### Task 4: Ember Study token layer (light + dark) + reduced-motion guard

**Files:**
- Modify: `WebChat.Client/wwwroot/css/app.css` — replace the `:root` block (lines ~6–62) and the `[data-theme="dark"]` block (lines ~64–116); add a reduced-motion guard in the base styles section.

Pure CSS, reusing **every existing variable name** (so all components reskin via the cascade). No unit test — verified by build + visual QA in both themes. The key changes: `--accent` becomes ember; a new `--space-accent` defaults to ember; `--user-bg` becomes solid ember (the old purple gradient is gone); warm paper/espresso neutrals.

- [ ] **Step 1: Replace the `:root` (light) token block**

Replace the entire current `:root { ... }` block with:

```css
:root {
    /* Backgrounds — warm paper */
    --bg-primary: #f3ede1;
    --bg-secondary: #fffaf1;
    --bg-tertiary: #ece4d4;
    --bg-elevated: #fffaf1;

    /* Text — warm ink */
    --text-primary: #2a2118;
    --text-secondary: #7a6a52;
    --text-muted: #a08a68;

    /* Accent — ember is the single constant action color */
    --accent: #e9601f;
    --accent-hover: #cf5214;
    --accent-light: #f7d9c4;
    --accent-subtle: #fbeee2;

    /* Per-space tint — defaults to ember (subtle differentiation only when a space overrides it) */
    --space-accent: var(--accent);

    /* Message Bubbles — solid ember user bubble (no gradient) */
    --user-bg: #e9601f;
    --user-text: #fff3e9;
    --assistant-bg: #fffaf1;
    --assistant-border: #e6dbc6;

    /* UI Elements */
    --border-color: #e6dbc6;
    --border-light: #efe7d7;
    --input-bg: #fffaf1;
    --input-border: #ddd0b8;
    --input-focus: #e9601f;

    /* Scrollbar */
    --scrollbar-thumb: #d9c9ad;
    --scrollbar-thumb-hover: #c2ad8a;
    --scrollbar-track: transparent;

    /* Shadows — warm-tinted */
    --shadow-sm: 0 1px 2px rgba(60, 40, 20, 0.06);
    --shadow-md: 0 4px 6px -1px rgba(60, 40, 20, 0.12), 0 2px 4px -1px rgba(60, 40, 20, 0.07);
    --shadow-lg: 0 10px 15px -3px rgba(60, 40, 20, 0.14), 0 4px 6px -2px rgba(60, 40, 20, 0.08);
    --shadow-message: 0 1px 3px rgba(60, 40, 20, 0.10);

    /* Status Colors */
    --success: #5aa06e;
    --error: #c0492f;
    --error-bg: #fbe7df;
    --error-border: #f0c4b4;

    /* Code Blocks */
    --code-bg: #efe6d4;
    --code-border: #e6dbc6;

    /* Transitions */
    --transition-fast: 150ms ease;
    --transition-normal: 200ms ease;
    --transition-slow: 300ms ease;
}
```

- [ ] **Step 2: Replace the `[data-theme="dark"]` (dark) token block**

Replace the entire current `[data-theme="dark"] { ... }` block with:

```css
[data-theme="dark"] {
    /* Backgrounds — warm espresso */
    --bg-primary: #171310;
    --bg-secondary: #211b15;
    --bg-tertiary: #2c241c;
    --bg-elevated: #241e17;

    /* Text */
    --text-primary: #efe4d2;
    --text-secondary: #c2b49a;
    --text-muted: #a4937a;

    /* Accent — ember, slightly brighter for dark contrast */
    --accent: #f0712f;
    --accent-hover: #f3884c;
    --accent-light: #3a2417;
    --accent-subtle: #2a1d13;

    /* Per-space tint */
    --space-accent: var(--accent);

    /* Message Bubbles */
    --user-bg: #f0712f;
    --user-text: #fff3e9;
    --assistant-bg: #241e17;
    --assistant-border: #3a2f24;

    /* UI Elements */
    --border-color: #3a2f24;
    --border-light: #241e17;
    --input-bg: #241e17;
    --input-border: #4a3d2e;
    --input-focus: #f0712f;

    /* Scrollbar */
    --scrollbar-thumb: #4a3d2e;
    --scrollbar-thumb-hover: #6a5a44;
    --scrollbar-track: transparent;

    /* Shadows */
    --shadow-sm: 0 1px 2px rgba(0, 0, 0, 0.4);
    --shadow-md: 0 4px 6px -1px rgba(0, 0, 0, 0.5), 0 2px 4px -1px rgba(0, 0, 0, 0.4);
    --shadow-lg: 0 10px 15px -3px rgba(0, 0, 0, 0.5), 0 4px 6px -2px rgba(0, 0, 0, 0.4);
    --shadow-message: 0 2px 8px rgba(0, 0, 0, 0.35);

    /* Status Colors */
    --success: #6cc08a;
    --error: #f0795a;
    --error-bg: #2e1813;
    --error-border: #5a2c1f;

    /* Code Blocks */
    --code-bg: #120f0c;
    --code-border: #3a2f24;
}
```

- [ ] **Step 3: Add the global reduced-motion guard**

In `app.css`, immediately after the `* { box-sizing: border-box; }` base rule, add:

```css
@media (prefers-reduced-motion: reduce) {
    *,
    *::before,
    *::after {
        animation-duration: 0.01ms !important;
        animation-iteration-count: 1 !important;
        transition-duration: 0.01ms !important;
        scroll-behavior: auto !important;
    }
}
```

- [ ] **Step 4: Build and visually verify both themes**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

Run the app and confirm: light theme is warm paper with ember accents; toggling the theme (header button) gives warm espresso (not blue-slate); user bubbles are solid ember (no purple gradient); code blocks, inputs, topic rows, toasts all read warm. With OS "reduce motion" on, animations/transitions are suppressed.

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/wwwroot/css/app.css
git commit -m "feat(webchat): Ember Study token layer (warm light + espresso dark) + reduced-motion guard"
```

---

### Task 5: Wire `SpaceState.AccentColor` into `--space-accent`

**Files:**
- Modify: `WebChat.Client/wwwroot/app.js` (add `accentHelper`), `WebChat.Client/Layout/MainLayout.razor`

Today `AccentColor` only drives the favicon (`faviconHelper.setColor`) and the header logo (`icon.svg?color=`). This adds the CSS chain so a space with a custom accent tints `--space-accent` (used by Plan C for the agent active-ring, etc.). No unit test — JS interop; verified visually.

- [ ] **Step 1: Add `accentHelper` to `app.js`**

After the `faviconHelper` block, add:

```js
// ===================================
// Per-space accent (CSS custom property)
// ===================================

window.accentHelper = {
    setVar: function (color) {
        if (!/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.test(color)) return;
        document.documentElement.style.setProperty('--space-accent', color);
    }
};
```

- [ ] **Step 2: Call it from `MainLayout` where the accent changes**

In `MainLayout.razor`, the `SpaceStore` subscription already runs an `InvokeAsync` block that calls `faviconHelper.setColor`. Add the `accentHelper.setVar` call next to it:

```csharp
                InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    var displayName = _spaceName == "Main" ? null : _spaceName;
                    await Js.InvokeVoidAsync("faviconHelper.setColor", _accentColor);
                    await Js.InvokeVoidAsync("accentHelper.setVar", _accentColor);
                    await Js.InvokeVoidAsync("faviconHelper.setSpaceTitle", displayName);
                    StateHasChanged();
                });
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

Verify in the browser: on the default space, `getComputedStyle(document.documentElement).getPropertyValue('--space-accent')` resolves to the ember default; visiting a space whose configured `AccentColor` differs sets `--space-accent` to that color (inspect the inline style on `<html>`).

- [ ] **Step 4: Commit**

```bash
git add WebChat.Client/wwwroot/app.js WebChat.Client/Layout/MainLayout.razor
git commit -m "feat(webchat): wire per-space AccentColor into the --space-accent CSS variable"
```

---

### Task 6: Warm monogram palette

**Files:**
- Modify: `WebChat.Client/Helpers/AvatarHelper.cs` (the `_colors` array)
- Test: `Tests/Unit/WebChat.Client/Helpers/AvatarHelperTests.cs` (create)

`AvatarHelper.GetColorForUser` (used for user avatar fallbacks, and by Plan C for agent monograms) currently picks from 8 saturated cold/candy hexes. Swap for a warm Ember-Study ramp. Determinism and the hashing logic are unchanged.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/WebChat.Client/Helpers/AvatarHelperTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.Helpers;

namespace Tests.Unit.WebChat.Client.Helpers;

public sealed class AvatarHelperTests
{
    private static readonly string[] WarmPalette =
    [
        "#E9601F", "#C2693B", "#B5611F", "#A8743A",
        "#9A5B4A", "#7C6A3F", "#3F7D6E", "#8A5A3C"
    ];

    [Fact]
    public void GetColorForUser_IsDeterministic()
    {
        AvatarHelper.GetColorForUser("kakera").ShouldBe(AvatarHelper.GetColorForUser("kakera"));
    }

    [Fact]
    public void GetColorForUser_AlwaysReturnsAWarmPaletteColor()
    {
        var seeds = Enumerable.Range(0, 60).Select(i => $"agent-{i}");
        seeds.Select(AvatarHelper.GetColorForUser)
            .ShouldAllBe(color => WarmPalette.Contains(color));
    }

    [Fact]
    public void GetInitials_TwoWords_ReturnsTwoUppercaseInitials()
    {
        AvatarHelper.GetInitials("Ada Lovelace").ShouldBe("AL");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AvatarHelperTests"`
Expected: FAIL — `GetColorForUser_AlwaysReturnsAWarmPaletteColor` fails (current colors like `#4ECDC4` are not in the warm set). (`GetInitials` and determinism already pass.)

- [ ] **Step 3: Replace the palette**

In `WebChat.Client/Helpers/AvatarHelper.cs`, replace the `_colors` array with:

```csharp
    private static readonly string[] _colors =
    [
        "#E9601F",
        "#C2693B",
        "#B5611F",
        "#A8743A",
        "#9A5B4A",
        "#7C6A3F",
        "#3F7D6E",
        "#8A5A3C"
    ];
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AvatarHelperTests"`
Expected: PASS (all three facts).

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/Helpers/AvatarHelper.cs Tests/Unit/WebChat.Client/Helpers/AvatarHelperTests.cs
git commit -m "feat(webchat): warm Ember monogram palette for avatars"
```

---

### Task 7: Verification & visual QA

**Files:** none (verification only)

- [ ] **Step 1: Confirm no stray brand colors remain**

Run: `grep -rn "6366f1\|8b5cf6\|818cf8\|7c3aed\|e94560" WebChat.Client --include=*.css --include=*.razor`
Expected: no matches (all indigo/violet/old-pink values are gone; everything routes through the warm tokens).

- [ ] **Step 2: Full unit run for the two changed C# units**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpaceConfigTests|FullyQualifiedName~AvatarHelperTests"`
Expected: PASS.

- [ ] **Step 3: Manual visual checklist (run the app, both themes)**

Confirm: fonts are Fraunces (names/headings) + Hanken Grotesk (body) + JetBrains Mono (timestamps/code), all served locally; light = warm paper, dark = warm espresso; ember accents on buttons/focus/links/send; user bubble solid ember; topic sidebar, approval modal, toasts, empty state, suggestion chips, identity picker all read warm (they inherit the tokens); no console requests to `fonts.googleapis.com`.

- [ ] **Step 4: Regression smoke (optional, needs the Docker stack + `OPENROUTER__APIKEY`)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.SendMessage_AppearsInChat"`
Expected: PASS or `Skip` if the stack is unavailable. Confirms the reskin didn't break the core send/receive flow.

---

## Self-Review

- **Spec coverage (Plan A scope = spec §4 visual system, §6.3 prereq 1, §6.7 fonts/SW, §4.1 accent split, monograms):** fonts self-hosted + SW fix (Tasks 1–2), warm tokens + `--accent`=ember + `--space-accent` + reduced-motion (Task 4), accent wiring (Task 5), warm monograms (Task 6), warm default accent (Task 3). All covered. Deferred to Plan C by design: `.topic-*` structural CSS, the Hearth components. Deferred to Plan B: cross-agent activity state.
- **Placeholder scan:** none — every code step shows complete content; font acquisition names exact files/sources.
- **Type consistency:** `--space-accent` named identically across Task 4 (CSS default) and Task 5 (JS `setProperty`); the warm palette in the Task 6 test matches the array set in the implementation; `SpaceConfig.DefaultAccentColor` value pinned in Task 3 matches the appsettings update.
- **No-bUnit constraint honored:** the only `[Fact]` tests are over pure C# (`SpaceConfig`, `AvatarHelper`); CSS/asset/JS steps use build + visual + existing E2E for verification, never a fictional component test.
