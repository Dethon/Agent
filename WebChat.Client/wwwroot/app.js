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
    // Check if user is scrolled to near the bottom (within threshold)
    isAtBottom: function (element) {
        if (!element) return true;
        const threshold = 50; // pixels from bottom to consider "at bottom"
        return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
    },

    // Scroll element to the bottom with smooth animation
    scrollToBottom: function (element, smooth) {
        if (!element) return;
        if (smooth) {
            element.scrollTo({
                top: element.scrollHeight,
                behavior: 'smooth'
            });
        } else {
            element.scrollTop = element.scrollHeight;
        }
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

window.isTextTruncated = function (element) {
    if (!element) return false;
    return element.scrollWidth > element.clientWidth;
};
