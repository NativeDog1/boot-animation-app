/**
 * 续传测试服务器：模拟"下到一半连接被掐断"。
 *
 *   node tools-serve-test.mjs <文件路径> <端口> <前几次故意掐断> <掐断前的字节数>
 *
 * 行为：
 *   - 支持 Range（206 + Content-Range），所以客户端**能**续传；
 *   - 前 N 次请求在发到指定字节数后直接 destroy 连接，制造中断；
 *   - 之后正常发完。
 * 用来验证客户端的断点续传与退避重试真的有效，而不是只"编译通过"。
 */
import { createServer } from 'node:http';
import { readFileSync, statSync } from 'node:fs';

const file = process.argv[2];
const port = Number(process.argv[3] || 8899);
const killFirst = Number(process.argv[4] || 2);
const killAfter = Number(process.argv[5] || 1024 * 1024);

const total = statSync(file).size;
const body = readFileSync(file);
let requests = 0;

createServer((req, res) => {
  requests += 1;
  const n = requests;
  const range = req.headers.range;
  let start = 0;
  let end = total - 1;
  let status = 200;

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
  console.log(`[srv] #${n} range=${range || '(none)'} → ${status} start=${start} len=${length}${willKill ? ' 【本例故意掐断】' : ''}`);

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

  // 故意只发一段就掐断
  const cut = Math.min(length, killAfter);
  res.write(body.subarray(start, start + cut));
  setTimeout(() => {
    console.log(`[srv] #${n} 已发送 ${cut} 字节，掐断连接`);
    res.destroy();
  }, 120);
}).listen(port, '127.0.0.1', () => {
  console.log(`[srv] ${file} (${total} 字节) 监听 http://127.0.0.1:${port}/x.mp4，前 ${killFirst} 次请求在 ${killAfter} 字节后掐断`);
});
