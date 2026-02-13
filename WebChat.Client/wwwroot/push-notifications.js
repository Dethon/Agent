window.pushNotifications = {
    async requestPermission() {
        if (!('Notification' in window)) return 'denied';
        return await Notification.requestPermission();
    },

    async subscribe(vapidPublicKey) {
        if (!('serviceWorker' in navigator)) return null;
        console.log('[Push] subscribe called, vapidKey length:', vapidPublicKey?.length);
        const registration = await navigator.serviceWorker.ready;

        // Force-refresh: unsubscribe existing to get a fresh push channel
        // This fixes stale WNS channels that accept pushes (201) but never deliver
        const existing = await registration.pushManager.getSubscription();
        const oldEndpoint = existing?.endpoint ?? null;
        console.log('[Push] existing subscription:', oldEndpoint ? oldEndpoint.substring(0, 60) + '...' : 'none');
        if (existing) {
            await existing.unsubscribe();
            console.log('[Push] unsubscribed old subscription');
        }

        const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);
        console.log('[Push] applicationServerKey bytes:', applicationServerKey.length);
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey
        });
        const json = subscription.toJSON();
        console.log('[Push] new subscription endpoint:', json.endpoint?.substring(0, 60) + '...');
        console.log('[Push] endpoint changed:', oldEndpoint !== json.endpoint);
        return {
            endpoint: json.endpoint,
            p256dh: json.keys.p256dh,
            auth: json.keys.auth,
            oldEndpoint: oldEndpoint !== json.endpoint ? oldEndpoint : null
        };
    },

    async isSubscribed() {
        if (!('serviceWorker' in navigator)) return false;
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        return subscription !== null;
    },

    async unsubscribe() {
        if (!('serviceWorker' in navigator)) return null;
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        if (subscription) {
            const endpoint = subscription.endpoint;
            await subscription.unsubscribe();
            return endpoint;
        }
        return null;
    },

    _urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - base64String.length % 4) % 4);
        const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        const rawData = window.atob(base64);
        return Uint8Array.from([...rawData].map(char => char.charCodeAt(0)));
    }
};
