// Strips the juggler viewport fields that playwright-core 1.61 added but the
// Camoufox Firefox build's juggler does not describe in its protocol scheme.
//
// playwright-core 1.61 started sending `screenSize` and `isMobile` on both
// Browser.setDefaultViewport and Page.setViewportSize. Camoufox's juggler
// (v152.0.4-beta.27) declares:
//   pageTypes.Viewport = { viewportSize, deviceScaleFactor? }
//   'setViewportSize': { params: { viewportSize } }
// and its Dispatcher validates params strictly, so any undescribed property
// fails the call with:
//   Found property "<root>.viewport.isMobile" - false which is not described in this scheme
// Removing the two fields restores the exact 1.60 wire shape, which Camoufox accepts.
const fs = require('fs');

const file = process.env.PW_CORE_BUNDLE || '/app/node_modules/playwright-core/lib/coreBundle.js';

const patches = [
  {
    name: 'Browser.setDefaultViewport',
    needle:
      '        const viewport = {\n' +
      '          viewportSize: { width: this._options.viewport.width, height: this._options.viewport.height },\n' +
      '          screenSize: this._options.screen,\n' +
      '          deviceScaleFactor: this._options.deviceScaleFactor || 1,\n' +
      '          isMobile: !!this._options.isMobile\n' +
      '        };',
    replacement:
      '        const viewport = {\n' +
      '          viewportSize: { width: this._options.viewport.width, height: this._options.viewport.height },\n' +
      '          deviceScaleFactor: this._options.deviceScaleFactor || 1\n' +
      '        };',
  },
  {
    name: 'Page.setViewportSize',
    needle:
      '        await this._session.send("Page.setViewportSize", {\n' +
      '          viewportSize: emulatedSize?.viewport ?? null,\n' +
      '          screenSize: emulatedSize?.screen,\n' +
      '          isMobile: !!this._browserContext._options.isMobile\n' +
      '        });',
    replacement:
      '        await this._session.send("Page.setViewportSize", {\n' +
      '          viewportSize: emulatedSize?.viewport ?? null\n' +
      '        });',
  },
];

let src = fs.readFileSync(file, 'utf8');
for (const p of patches) {
  if (!src.includes(p.needle)) {
    console.error(
      `patch-viewport: anchor not found for ${p.name} — playwright-core internals changed. ` +
      'Re-verify whether Camoufox\'s juggler now describes screenSize/isMobile and update this guard.');
    process.exit(1);
  }
  src = src.split(p.needle).join(p.replacement);
}
fs.writeFileSync(file, src);
console.log('patch-viewport: stripped screenSize/isMobile from juggler viewport calls.');
