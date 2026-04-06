using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class AccessibilitySnapshotService
{
    private const string SnapshotScript = """
        (selectorScope) => {
            let refCounter = 0;

            const implicitRoles = {
                'A': (el) => el.hasAttribute('href') ? 'link' : null,
                'BUTTON': () => 'button',
                'INPUT': (el) => {
                    const type = (el.getAttribute('type') || 'text').toLowerCase();
                    return ({ text:'textbox', search:'searchbox', email:'textbox', password:'textbox',
                        tel:'textbox', url:'textbox', number:'spinbutton', checkbox:'checkbox',
                        radio:'radio', range:'slider', submit:'button', reset:'button',
                        button:'button', image:'button' })[type] || 'textbox';
                },
                'SELECT': () => 'combobox',
                'TEXTAREA': () => 'textbox',
                'H1': () => 'heading', 'H2': () => 'heading', 'H3': () => 'heading',
                'H4': () => 'heading', 'H5': () => 'heading', 'H6': () => 'heading',
                'IMG': () => 'img',
                'NAV': () => 'navigation', 'MAIN': () => 'main',
                'HEADER': () => 'banner', 'FOOTER': () => 'contentinfo',
                'ASIDE': () => 'complementary', 'FORM': () => 'form',
                'TABLE': () => 'table', 'THEAD': () => 'rowgroup', 'TBODY': () => 'rowgroup',
                'TH': () => 'columnheader', 'TD': () => 'cell', 'TR': () => 'row',
                'UL': () => 'list', 'OL': () => 'list', 'LI': () => 'listitem',
                'OPTION': () => 'option', 'DIALOG': () => 'dialog',
                'DETAILS': () => 'group', 'SUMMARY': () => 'button',
                'PROGRESS': () => 'progressbar', 'METER': () => 'meter'
            };

            const interactiveRoles = new Set([
                'button','link','textbox','searchbox','combobox','checkbox','radio',
                'slider','spinbutton','switch','tab','menuitem','menuitemcheckbox',
                'menuitemradio','option','treeitem'
            ]);

            const structuralRoles = new Set([
                'heading','navigation','main','banner','contentinfo','complementary',
                'form','dialog','list','listitem','table','row','cell','columnheader',
                'img','group','tablist','tabpanel','menu','menubar','toolbar','tree',
                'grid','listbox','progressbar','meter','alert','status','region','rowgroup'
            ]);

            function getRole(el) {
                const explicit = el.getAttribute('role');
                if (explicit) return explicit;
                const fn = implicitRoles[el.tagName];
                return fn ? fn(el) : null;
            }

            function getName(el) {
                const ariaLabel = el.getAttribute('aria-label');
                if (ariaLabel) return ariaLabel.trim();

                const labelledBy = el.getAttribute('aria-labelledby');
                if (labelledBy) {
                    const text = labelledBy.split(/\s+/)
                        .map(id => document.getElementById(id)?.textContent?.trim())
                        .filter(Boolean).join(' ');
                    if (text) return text;
                }

                if (el.id) {
                    const label = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
                    if (label) return label.textContent.trim();
                }

                const parentLabel = el.closest('label');
                if (parentLabel && parentLabel !== el) {
                    const clone = parentLabel.cloneNode(true);
                    clone.querySelectorAll('input,select,textarea').forEach(c => c.remove());
                    const text = clone.textContent.trim();
                    if (text) return text;
                }

                const placeholder = el.getAttribute('placeholder');
                if (placeholder) return placeholder.trim();

                const title = el.getAttribute('title');
                if (title) return title.trim();

                const role = getRole(el);
                if (['button','link','heading','tab','menuitem','option','cell','columnheader'].includes(role)) {
                    const text = el.textContent?.trim();
                    if (text && text.length <= 80) return text;
                    if (text) return text.substring(0, 77) + '...';
                }

                if (el.tagName === 'IMG') return el.getAttribute('alt')?.trim() || null;
                return null;
            }

            function getState(el) {
                const s = [];
                if (el.getAttribute('aria-expanded') === 'true') s.push('expanded');
                if (el.getAttribute('aria-expanded') === 'false') s.push('collapsed');
                if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') s.push('disabled');
                if (el.hasAttribute('required') || el.getAttribute('aria-required') === 'true') s.push('required');
                if (el.getAttribute('aria-checked') === 'true' || el.checked) s.push('checked');
                if (el.getAttribute('aria-selected') === 'true' || el.selected) s.push('selected');
                if (el.getAttribute('aria-pressed') === 'true') s.push('pressed');
                if (el.hasAttribute('readonly') || el.getAttribute('aria-readonly') === 'true') s.push('readonly');
                const hm = el.tagName.match(/^H(\d)$/);
                if (hm) s.push('level=' + hm[1]);
                return s;
            }

            function getValue(el) {
                if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return el.value || null;
                if (el.tagName === 'SELECT') {
                    const opt = el.options[el.selectedIndex];
                    return opt ? opt.text : null;
                }
                return null;
            }

            function isHidden(el) {
                if (el.tagName === 'SCRIPT' || el.tagName === 'STYLE' || el.tagName === 'NOSCRIPT') return true;
                if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
                const cs = getComputedStyle(el);
                return cs.display === 'none' || cs.visibility === 'hidden';
            }

            function isVisible(el) {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 || rect.height > 0 ||
                       getComputedStyle(el).position === 'fixed';
            }

            function buildTree(el) {
                if (isHidden(el)) return null;
                const role = getRole(el);
                const children = Array.from(el.children)
                    .map(c => buildTree(c)).filter(Boolean);

                if (!role && children.length === 1) return children[0];
                if (!role && children.length === 0) return null;

                const isKnown = role && (interactiveRoles.has(role) || structuralRoles.has(role));
                if (isKnown) {
                    let ref = null;
                    if (interactiveRoles.has(role) && isVisible(el)) {
                        refCounter++;
                        ref = 'e' + refCounter;
                        el.setAttribute('data-ref', ref);
                    }
                    return { role, name: getName(el), state: getState(el),
                             value: getValue(el), ref, children };
                }

                return children.length > 0 ? { role: null, children } : null;
            }

            function format(node, indent) {
                if (!node) return '';
                if (!node.role) {
                    return node.children.map(c => format(c, indent)).filter(Boolean).join('\n');
                }
                let line = '  '.repeat(indent) + '- ' + node.role;
                if (node.name) line += ' "' + node.name.replace(/"/g, '\\"') + '"';
                if (node.ref) line += ' [ref=' + node.ref + ']';
                if (node.value) line += ': ' + node.value;
                for (const s of node.state) line += ' [' + s + ']';
                const cl = node.children.map(c => format(c, indent + 1)).filter(Boolean);
                return cl.length > 0 ? line + '\n' + cl.join('\n') : line;
            }

            const root = selectorScope ? document.querySelector(selectorScope) : document.body;
            if (!root) return { snapshot: '', refCount: 0 };
            const tree = buildTree(root);
            return { snapshot: tree ? format(tree, 0) : '', refCount: refCounter };
        }
        """;

    private const string FindContainerScript = """
        (selector) => {
            const el = document.querySelector(selector);
            if (!el) return null;
            const tags = ['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET'];
            const roles = ['dialog','form','region','listbox','menu'];
            let container = el.parentElement;
            for (let i = 0; i < 4 && container; i++) {
                if (tags.includes(container.tagName) ||
                    roles.includes(container.getAttribute('role'))) {
                    if (container.id) return '#' + CSS.escape(container.id);
                    const idx = Array.from(container.parentElement?.children || [])
                        .filter(c => c.tagName === container.tagName).indexOf(container);
                    return container.tagName.toLowerCase() +
                        ':nth-of-type(' + (idx + 1) + ')';
                }
                container = container.parentElement;
            }
            return null;
        }
        """;

    public async Task<SnapshotCaptureResult> CaptureAsync(
        IPage page, string? selectorScope, string sessionId)
    {
        var result = await page.EvaluateAsync<SnapshotJsResult>(SnapshotScript, selectorScope);
        return new SnapshotCaptureResult(result.Snapshot, result.RefCount);
    }

    public async Task<SnapshotCaptureResult> CaptureScopedAsync(
        IPage page, string targetSelector, string sessionId)
    {
        var containerSelector = await page.EvaluateAsync<string?>(
            FindContainerScript, targetSelector);

        return await CaptureAsync(page, containerSelector, sessionId);
    }

    public static ILocator ResolveRef(IPage page, string @ref)
        => page.Locator($"[data-ref='{@ref}']");

    public record SnapshotCaptureResult(string Snapshot, int RefCount);
    private record SnapshotJsResult(string Snapshot, int RefCount);
}
