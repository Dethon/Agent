window.pushNotifications = {
    async requestPermission() {
        if (!('Notification' in window)) return 'denied';
        return await Notification.requestPermission();
    },

    async subscribe(vapidPublicKey) {
        if (!('serviceWorker' in navigator)) return null;
        const registration = await navigator.serviceWorker.ready;

        // Force-refresh: unsubscribe existing to get a fresh push channel
        const existing = await registration.pushManager.getSubscription();
        const oldEndpoint = existing?.endpoint ?? null;
        if (existing) {
            await existing.unsubscribe();
        }

        const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey
        });
        const json = subscription.toJSON();
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
