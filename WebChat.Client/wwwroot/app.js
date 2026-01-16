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
