/**
 * Example Electron main-process health endpoint for Kiosk Watchdog.
 * Bind to 127.0.0.1 only. Report healthy only after critical startup finishes.
 *
 * Usage (main.js / main.ts):
 *   const { startHealthServer } = require('./health-server');
 *   // after app ready + critical init:
 *   startHealthServer(3000);
 */

const http = require('http');

function startHealthServer(port = 3000) {
  let ready = false;

  const server = http.createServer((req, res) => {
    if (req.url === '/health' || req.url === '/health/') {
      if (!ready) {
        res.writeHead(503, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ status: 'starting' }));
        return;
      }

      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ status: 'ok' }));
      return;
    }

    res.writeHead(404, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'not found' }));
  });

  server.listen(port, '127.0.0.1', () => {
    console.log(`Kiosk health listening on http://127.0.0.1:${port}/health`);
  });

  return {
    markReady() {
      ready = true;
    },
    close() {
      return new Promise((resolve) => server.close(resolve));
    }
  };
}

module.exports = { startHealthServer };
