// ===================================
// Theme Management
// ===================================

window.themeManager = {
    // Get the current theme
    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'light';
    },

    // Set theme and persist to localStorage
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem('theme', theme);
        return theme;
    },

    // Toggle between light and dark
    toggleTheme: function () {
        const current = this.getTheme();
        const next = current === 'dark' ? 'light' : 'dark';
        return this.setTheme(next);
    },

    // Initialize theme from localStorage or system preference
    init: function () {
        const stored = localStorage.getItem('theme');
        if (stored) {
            document.documentElement.setAttribute('data-theme', stored);
            return stored;
        }

        // Check system preference
        if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            document.documentElement.setAttribute('data-theme', 'dark');
            return 'dark';
        }

        return 'light';
    }
};

// Initialize theme immediately to prevent flash
themeManager.init();

// Listen for system theme changes
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) {
        // Only auto-switch if user hasn't set a preference
        if (!localStorage.getItem('theme')) {
            themeManager.setTheme(e.matches ? 'dark' : 'light');
        }
    });
}

// ===================================
// Page Visibility (for mobile background/foreground)
// ===================================

window.visibilityHelper = {
    _dotnetRef: null,

    register: function (dotnetRef) {
        this._dotnetRef = dotnetRef;
        document.addEventListener('visibilitychange', this._handler);
    },

    _handler: function () {
        const ref = window.visibilityHelper._dotnetRef;
        if (ref && document.visibilityState === 'visible') {
            ref.invokeMethodAsync('OnPageVisible');
        }
    },

    dispose: function () {
        document.removeEventListener('visibilitychange', this._handler);
        this._dotnetRef = null;
    }
};

// ===================================
// Chat Input Handling
// ===================================

// Prevent Enter from adding newlines in chat input (Shift+Enter still works)
document.addEventListener('keydown', function (e) {
    if (e.target.classList.contains('chat-input') && e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
    }
});

// Auto-resize textarea utilities
window.chatInput = {
    // Auto-resize textarea to fit content
    autoResize: function (element) {
        if (!element) return;

        // Reset height to auto to get the correct scrollHeight
        element.style.height = 'auto';

        // Set the height to match content (respects max-height from CSS)
        element.style.height = element.scrollHeight + 'px';
    },

    // Reset textarea to minimum height
    reset: function (element) {
        if (!element) return;
        element.style.height = 'auto';
    }
};

// Auto-resize on input event
document.addEventListener('input', function (e) {
    if (e.target.classList.contains('chat-input')) {
        window.chatInput.autoResize(e.target);
    }
});

// ===================================
// Chat Scroll Utilities
// ===================================

window.chatScroll = {
    _stickyState: true,  // Track if we should stick to bottom
    _element: null,
    _scrollHandler: null,

    // Check if user is scrolled to near the bottom (within threshold)
    isAtBottom: function (element) {
        if (!element) return true;
        const threshold = 50; // pixels from bottom to consider "at bottom"
        return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
    },

    // Scroll element to the bottom with smooth animation
    scrollToBottom: function (element, smooth) {
        if (!element) return;
        this._stickyState = true;  // Reset sticky state when forcing scroll
        requestAnimationFrame(() => {
            if (smooth) {
                element.scrollTo({
                    top: element.scrollHeight,
                    behavior: 'smooth'
                });
            } else {
                element.scrollTop = element.scrollHeight;
            }
        });
    },

    // Initialize scroll tracking for an element
    initStickyScroll: function (element) {
        if (!element) return;

        // Clean up previous handler if exists
        if (this._element && this._scrollHandler) {
            this._element.removeEventListener('scroll', this._scrollHandler);
        }

        this._element = element;
        this._stickyState = true;

        // Track scroll position to detect user scrolling away
        const self = this;
        this._scrollHandler = () => {
            self._stickyState = self.isAtBottom(element);
        };

        element.addEventListener('scroll', this._scrollHandler, {passive: true});
    },

    // Scroll to bottom only if sticky state is true
    scrollToBottomIfSticky: function (element) {
        if (!element) return;
        if (this._stickyState) {
            requestAnimationFrame(() => {
                element.scrollTop = element.scrollHeight;
            });
        }
    },

    // Dispose scroll tracking
    dispose: function () {
        if (this._element && this._scrollHandler) {
            this._element.removeEventListener('scroll', this._scrollHandler);
        }
        this._element = null;
        this._scrollHandler = null;
        this._stickyState = true;
    }
};

// ===================================
// Favicon
// ===================================

window.faviconHelper = {
    _baseTitle: document.title,

    setColor: function (color) {
        if (!/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.test(color)) return;
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 50" width="120" height="50"><text x="60" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="40" fill="${color}">ᓚᘏᗢ</text></svg>`;
        const link = document.querySelector('link[rel="icon"]');
        if (link) {
            link.href = 'data:image/svg+xml,' + encodeURIComponent(svg);
        }
    },

    setSpaceTitle: function (spaceName) {
        document.title = spaceName
            ? this._baseTitle + ' \u2014 ' + spaceName
            : this._baseTitle;
    }
};

// ===================================
// Element Utilities
// ===================================

window.getBoundingClientRect = function (element) {
    if (!element) return {top: 0, left: 0, width: 0, height: 0};
    const rect = element.getBoundingClientRect();
    return {
        top: rect.top,
        left: rect.left,
        width: rect.width,
        height: rect.height
    };
};

// ===================================
// Tooltip (lightweight)
// ===================================

(function () {
    let timeout;
    const tooltip = () => document.getElementById('tooltip');

    document.addEventListener('mouseenter', e => {
        const target = e.target.closest?.('[data-tooltip]');
        if (!target) return;

        const x = e.clientX, y = e.clientY;
        timeout = setTimeout(() => {
            const tip = tooltip();
            if (tip) {
                tip.textContent = target.dataset.tooltip;
                tip.style.left = (x) + 'px';
                tip.style.top = (y - 24) + 'px';
                tip.classList.add('visible');
            }
        }, 400);
    }, true);

    document.addEventListener('mouseleave', e => {
        if (e.target.closest?.('[data-tooltip]')) {
            clearTimeout(timeout);
            const tip = tooltip();
            if (tip) tip.classList.remove('visible');
        }
    }, true);
})();

