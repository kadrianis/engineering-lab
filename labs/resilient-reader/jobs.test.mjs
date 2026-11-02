import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { createJobsClient, JobReadError } from './jobs.mjs';

const id = 'a1234567-1234-1234-1234-123456789abc';
const job = { id, title: 'Synthetic export', status: 'pending' };
async function fixture(t, handler) {
  let calls = 0;
  const server = createServer((req, res) => handler(req, res, ++calls));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  });
  return { client: createJobsClient(`http://127.0.0.1:${server.address().port}`), calls: () => calls };
}

test('rejects untrusted origin shapes before I/O', () => {
  for (const origin of ['file:///tmp/a', 'http://example.com', 'https://u:p@example.com',
    'https://example.com/api', 'https://example.com/?token=x', 'https://example.com/#x', null])
    assert.throws(() => createJobsClient(origin));
  for (const origin of ['https://example.com', 'http://localhost:5080', 'http://[::1]:5080'])
    assert.ok(Object.isFrozen(createJobsClient(origin)));
});

test('invalid IDs cannot construct paths or URLs', async t => {
  const f = await fixture(t, () => assert.fail('Unexpected I/O'));
  for (const value of ['../health', '//evil.test', 'https://evil.test', `${id}?x=1`, '', null])
    await assert.rejects(f.client.getJob(value), TypeError);
  assert.equal(f.calls(), 0);
});

test('503 then matching job; normalizes ID and strips extra data', async t => {
  const f = await fixture(t, (req, res, n) => {
    assert.equal(req.method, 'GET'); assert.equal(req.url, `/jobs/${id}`);
    res.writeHead(n === 1 ? 503 : 200, { 'Retry-After': '0' });
    res.end(JSON.stringify({ ...job, id: id.toUpperCase(), internal: 'omit' }));
  });
  assert.deepEqual(await f.client.getJob(id.toUpperCase()), { kind: 'found', job, attempts: 2 });
  assert.equal(f.calls(), 2);
});

test('404 is a domain result without retry', async t => {
  const f = await fixture(t, (_, res) => { res.writeHead(404); res.end(); });
  assert.deepEqual(await f.client.getJob(id), { kind: 'not-found', attempts: 1 });
});

for (const status of [401, 403, 302, 500, 503]) {
  test(`HTTP ${status} yields typed error without leaking body`, async t => {
    const f = await fixture(t, (_, res) => {
      res.writeHead(status, { Location: 'http://127.0.0.1:1/secret', 'Retry-After': '0' });
      res.end('private error detail');
    });
    await assert.rejects(f.client.getJob(id), e => e instanceof JobReadError
      && e.code === 'HTTP_ERROR' && e.status === status && !e.message.includes('private'));
    assert.equal(f.calls(), status === 503 ? 3 : 1);
  });
}

for (const body of ['oops', 'null', '[]', '{}', JSON.stringify({ ...job, id: 'b' + id.slice(1) }),
  JSON.stringify({ ...job, title: ' ' }), JSON.stringify({ ...job, title: 'x'.repeat(121) }),
  JSON.stringify({ ...job, status: 'completed' })]) {
  test(`rejects invalid contract: ${body.slice(0, 65)}`, async t => {
    const f = await fixture(t, (_, res) => res.end(body));
    await assert.rejects(f.client.getJob(id), e => e instanceof JobReadError
      && e.code === (body === 'oops' ? 'INVALID_JSON' : 'INVALID_JOB'));
    assert.equal(f.calls(), 1);
  });
}

test('bounds response size', async t => {
  const f = await fixture(t, (_, res) => res.end('x'.repeat(8193)));
  await assert.rejects(f.client.getJob(id), RangeError);
});

test('honors cancellation before I/O', async t => {
  const f = await fixture(t, () => assert.fail('Unexpected I/O'));
  await assert.rejects(f.client.getJob(id, { signal: AbortSignal.abort() }), { name: 'AbortError' });
  assert.equal(f.calls(), 0);
});

test('global deadline includes a slow body', async t => {
  const f = await fixture(t, (_, res) => { res.writeHead(200); res.write('{'); });
  await assert.rejects(f.client.getJob(id, { timeoutMs: 100 }), { name: 'TimeoutError' });
});
