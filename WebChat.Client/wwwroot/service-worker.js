self.importScripts('./push-handler.js');

// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
// Note: No fetch event listener is registered - browsers warn about no-op handlers.
