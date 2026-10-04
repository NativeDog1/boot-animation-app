/**
 * serve-test.mjs — 测试用的 HTTP 服务器：支持 Range，并且能**故意在传输中途掐断连接**。
 *
 * 两种用法：
 *   node tools/serve-test.mjs <文件>  <端口> [前几次掐断] [掐断前字节数]
 *   node tools/serve-test.mjs <目录>  <端口> [前几次掐断] [掐断前字节数]   # 目录里每个文件按文件名提供
 *
 * 掐断次数传 0 就是正常服务器。用来验证客户端的断点续传、以及机器人的分片拼回 ——
 * 这两件事都只有在"连接真的断过"之后才能算数。
 */
import { createServer } from 'node:http';
import { readFileSync, statSync, existsSync } from 'node:fs';
import { join, basename } from 'node:path';

const target = process.argv[2];
const port = Number(process.argv[3] || 8899);
const killFirst = Number(process.argv[4] || 0);
const killAfter = Number(process.argv[5] || 1024 * 1024);

if (!target || !existsSync(target)) {
  console.error('用法: node tools/serve-test.mjs <文件或目录> <端口> [前几次掐断] [掐断前字节数]');
  process.exit(2);
}
const isDir = statSync(target).isDirectory();
let requests = 0;

createServer((req, res) => {
  requests += 1;
  const n = requests;
  const asked = decodeURIComponent((req.url || '/').split('?')[0]).replace(/^\/+/, '');
  if (isDir && !asked) { res.writeHead(400).end('need a file name'); return; }

  const file = isDir ? join(target, basename(asked)) : target;
  if (!existsSync(file) || statSync(file).isDirectory()) { res.writeHead(404).end('not found'); return; }

  const body = readFileSync(file);
  const total = body.length;
  let start = 0;
  let end = total - 1;
  let status = 200;

  const range = req.headers.range;
  if (range) {
    const m = /bytes=(\d+)-(\d*)/.exec(range);
    if (m) {
      start = Number(m[1]);
      if (m[2]) end = Number(m[2]);
      status = 206;
    }
  }

  const willKill = n <= killFirst;
  const length = end - start + 1;
  console.log(`[srv] #${n} ${basename(file)} range=${range || '(none)'} → ${status} len=${length}${willKill ? ' 【本例故意掐断】' : ''}`);

  const headers = {
    'content-type': 'video/mp4',
    'accept-ranges': 'bytes',
    'content-length': String(length),
  };
  if (status === 206) headers['content-range'] = `bytes ${start}-${end}/${total}`;
  res.writeHead(status, headers);

  if (!willKill) {
    res.end(body.subarray(start, end + 1));
    return;
  }
  const cut = Math.min(length, killAfter);
  res.write(body.subarray(start, start + cut));
  setTimeout(() => {
    console.log(`[srv] #${n} 已发送 ${cut} 字节，掐断连接`);
    res.destroy();
  }, 120);
}).listen(port, '127.0.0.1', () => {
  console.log(`[srv] ${isDir ? '目录' : '文件'} ${target} 监听 http://127.0.0.1:${port}/，掐断策略：前 ${killFirst} 次请求在 ${killAfter} 字节后断开`);
});
