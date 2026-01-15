// Prevent Enter from adding newlines in chat input (Shift+Enter still works)
document.addEventListener('keydown', function (e) {
    if (e.target.classList.contains('chat-input') && e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
    }
});
