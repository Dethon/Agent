self.addEventListener('push', event => {
    let data;
    try {
        data = event.data?.json();
    } catch {
        data = null;
    }
    data ??= { title: 'New message', body: '' };

    const spaceSlug = data.spaceSlug;
    let title = data.title;
    if (spaceSlug && spaceSlug !== 'default') {
        const spaceName = spaceSlug.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
        title = `${title} — ${spaceName}`;
    }

    const notificationUrl = data.url ?? '/';

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: false })
            .then(clients => {
                const isOnSpace = clients.some(
                    c => c.visibilityState === 'visible' && new URL(c.url).pathname === notificationUrl
                );
                if (isOnSpace) return;
                return self.registration.showNotification(title, {
                    body: data.body,
                    icon: '/icon.svg',
                    data: { url: notificationUrl }
                });
            })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            const existing = clients.find(c => new URL(c.url).origin === self.location.origin);
            if (existing) return existing.navigate(url).then(c => c.focus());
            return self.clients.openWindow(url);
        })
    );
});
