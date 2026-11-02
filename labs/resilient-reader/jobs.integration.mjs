import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
import { createJobsClient } from './jobs.mjs';

// Reserve an ephemeral port, then release it for Kestrel (small local race remains).
const reservation = createServer();
reservation.listen(0, '127.0.0.1');
await once(reservation, 'listening');
const origin = `http://127.0.0.1:${reservation.address().port}`;
await new Promise(resolve => reservation.close(resolve));
const root = fileURLToPath(new URL('../../', import.meta.url));
execFileSync('dotnet', ['build', 'labs/modular-jobs/Api', '-c', 'Release', '--nologo'],
  { cwd: root, timeout: 120000, stdio: 'pipe' });
// Run the DLL directly: cleanup must target the server, not a dotnet-run parent.
const child = spawn('dotnet', ['labs/modular-jobs/Api/bin/Release/net10.0/Api.dll', '--urls', origin], {
  cwd: root, stdio: ['ignore', 'pipe', 'pipe'],
});
let output = '';
child.stdout.on('data', chunk => { output = (output + chunk).slice(-12000); });
child.stderr.on('data', chunk => { output = (output + chunk).slice(-12000); });
let spawnError;
child.on('error', error => { spawnError = error; });
const exited = once(child, 'exit').catch(() => undefined);
try {
  const deadline = Date.now() + 90000;
  let ready = false;
  while (Date.now() < deadline) {
    if (spawnError) throw spawnError;
    if (child.exitCode !== null) throw Error(`API exited: ${output}`);
    try {
      const response = await fetch(`${origin}/health`, { signal: AbortSignal.timeout(500) });
      await response.text();
      if (response.ok) { ready = true; break; }
    } catch { /* startup */ }
    await sleep(100);
  }
  assert.ok(ready, `API startup timed out: ${output}`);
  // One synthetic creation; the GET client never retries a write.
  const created = await fetch(`${origin}/jobs`, { method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: 'Synthetic integration export' }),
    signal: AbortSignal.timeout(5000) });
  assert.equal(created.status, 201);
  const job = await created.json();
  const client = createJobsClient(origin);
  assert.deepEqual(await client.getJob(job.id), { kind: 'found', job, attempts: 1 });
  assert.deepEqual(await client.getJob('00000000-0000-0000-0000-000000000000'),
    { kind: 'not-found', attempts: 1 });
  console.log('PASS: real .NET API creation, validated GET, missing job (3 assertions).');
} finally {
  if (child.exitCode === null) {
    child.kill();
    await Promise.race([exited, sleep(5000)]);
    if (child.exitCode === null) { child.kill('SIGKILL'); await exited; }
  }
}
