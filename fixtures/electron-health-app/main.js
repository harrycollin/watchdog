const { app, BrowserWindow } = require("electron");
const http = require("http");

function readPort() {
  const arg = process.argv.find((a) => a.startsWith("--health-port="));
  if (arg) {
    const value = Number(arg.slice("--health-port=".length));
    if (Number.isFinite(value) && value > 0) return value;
  }
  const env = Number(process.env.HEALTH_PORT || 3921);
  return Number.isFinite(env) && env > 0 ? env : 3921;
}

const port = readPort();
let ready = false;

const server = http.createServer((req, res) => {
  const path = (req.url || "").split("?")[0];
  if (path === "/health" || path === "/health/") {
    const ok = ready;
    res.writeHead(ok ? 200 : 503, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ status: ok ? "ok" : "starting" }));
    return;
  }

  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ error: "not found" }));
});

server.listen(port, "127.0.0.1", () => {
  console.log(`electron-health-app listening on http://127.0.0.1:${port}/health`);
});

app.whenReady().then(() => {
  const win = new BrowserWindow({
    width: 320,
    height: 240,
    show: false,
    webPreferences: { sandbox: true }
  });
  win.loadURL("data:text/html,<title>KioskWatchdog Fixture</title><h1>ok</h1>");
  ready = true;
  console.log("electron-health-app ready");
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});
