// 在浏览器里预览界面：node tools/dev-server.mjs，然后打开 http://localhost:5317
// /       → src/WinSlim/wwwroot（真实界面）
// /dev/*  → src/WinSlim/dev（模拟后台，只用于预览）
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'WinSlim');
const port = Number(process.env.PORT) || 5317;
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8', '.svg': 'image/svg+xml' };

createServer(async (req, res) => {
  let path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (path === '/') path = '/index.html';
  const base = path.startsWith('/dev/') ? root : join(root, 'wwwroot');
  const file = normalize(join(base, path));
  if (!file.startsWith(root)) { res.writeHead(403).end(); return; }
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' }).end(body);
  } catch {
    res.writeHead(404).end('not found');
  }
}).listen(port, () => console.log(`WinSlim UI preview: http://localhost:${port}`));
