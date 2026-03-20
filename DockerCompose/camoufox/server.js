const { launchServer } = require('camoufox-js');

(async () => {
    const server = await launchServer({
        port: 9377,
        ws_path: '/browser',
        headless: 'virtual',
    });
    console.log('Camoufox server listening at:', server.wsEndpoint());
})();
