window.pushNotifications = {
    async requestPermission() {
        if (!('Notification' in window)) return 'denied';
        return await Notification.requestPermission();
    },

    async subscribe(vapidPublicKey) {
        const registration = await navigator.serviceWorker.ready;
        const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey
        });
        const json = subscription.toJSON();
        return {
            endpoint: json.endpoint,
            p256dh: json.keys.p256dh,
            auth: json.keys.auth
        };
    },

    async isSubscribed() {
        if (!('serviceWorker' in navigator)) return false;
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        return subscription !== null;
    },

    async unsubscribe() {
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        if (subscription) {
            const endpoint = subscription.endpoint;
            const unsubscribed = await subscription.unsubscribe();
            return unsubscribed ? endpoint : null;
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
