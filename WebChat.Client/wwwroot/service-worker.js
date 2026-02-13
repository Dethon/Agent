self.addEventListener('push', event => {
    console.log('[SW] push event fired, has data:', !!event.data);
    let data;
    try {
        data = event.data?.json();
        console.log('[SW] parsed push data:', JSON.stringify(data));
    } catch (e) {
        console.error('[SW] failed to parse push data:', e);
        data = null;
    }
    data ??= { title: 'New message', body: '' };
    event.waitUntil(
        self.registration.showNotification(data.title, {
            body: data.body,
            icon: '/icon.svg',
            data: { url: data.url }
        })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            const existing = clients.find(c => c.url.includes(url));
            return existing ? existing.focus() : self.clients.openWindow(url);
        })
    );
});

// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
// Note: No fetch event listener is registered - browsers warn about no-op handlers.
