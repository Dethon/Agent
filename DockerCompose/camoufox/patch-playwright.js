// Guards a playwright-core crash that takes down the whole Camoufox server process.
//
// Some pages emit an uncaught JS error with no source location (e.g. a cross-origin
// "Script error."). The Firefox backend's FFPage._onUncaughtError forwards that error to
// BrowserContext.addPageError with location === undefined. The protocol dispatcher then
// builds a "pageError" event reading location.url / location.lineNumber / location.columnNumber
// and validates url as a string. Both the dereference and the validation throw *synchronously
// inside the EventEmitter callback*, which Node treats as an uncaught exception — so the entire
// Playwright server process exits. That drops the WebSocket, and every in-flight (and cached)
// page/context/browser surfaces "Target page, context or browser has been closed" to callers.
//
// Defaulting the location to a valid, empty object keeps the dispatcher's field access and the
// protocol string/number validation happy, so the page error is delivered instead of crashing
// the server. The page then loads normally.
//
// If the anchor disappears after a playwright-core bump, the build fails loudly so we revisit
// the guard rather than silently shipping the crash again.
const fs = require('fs');

const file = '/app/node_modules/playwright-core/lib/coreBundle.js';
const needle = 'const pageError = { error, location: location2 };';
const replacement =
  'const pageError = { error, location: location2 || { url: "", lineNumber: 0, columnNumber: 0 } };';

const src = fs.readFileSync(file, 'utf8');
if (!src.includes(needle)) {
  console.error(
    'patch-playwright: anchor not found in coreBundle.js — playwright-core internals changed. ' +
    'Re-verify the PageError location-undefined crash and update this guard.');
  process.exit(1);
}

fs.writeFileSync(file, src.split(needle).join(replacement));
console.log('patch-playwright: applied PageError location guard.');
