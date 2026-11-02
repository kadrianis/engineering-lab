import { read } from './reader.mjs';

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export class JobReadError extends Error {
  constructor(code, status) {
    super(code);
    this.name = 'JobReadError';
    this.code = code;
    this.status = status;
  }
}

/** Trusted configuration only. This validates job identity, not user identity. */
export function createJobsClient(origin) {
  if (typeof origin !== 'string') throw new TypeError('Origin must be a string');
  const base = new URL(origin);
  if (!['https:', 'http:'].includes(base.protocol) || base.username || base.password
      || base.pathname !== '/' || base.search || base.hash)
    throw new TypeError('Expected an HTTP(S) origin without path, credentials, query or fragment');
  if (base.protocol === 'http:' && !['127.0.0.1', '[::1]', 'localhost'].includes(base.hostname))
    throw new TypeError('HTTP is allowed only for local development');
  // Capture a string so later caller mutation cannot redirect requests.
  const fixedOrigin = base.origin;
  return Object.freeze({
    async getJob(id, { signal, timeoutMs = 5000 } = {}) {
      if (typeof id !== 'string' || !guid.test(id))
        throw new TypeError('Expected a hyphenated GUID');
      const expectedId = id.toLowerCase();
      const result = await read(new URL(`/jobs/${expectedId}`, fixedOrigin), {
        signal, timeoutMs, maxBytes: 8192,
      });
      if (result.status === 404) return { kind: 'not-found', attempts: result.attempts };
      if (result.status !== 200) throw new JobReadError('HTTP_ERROR', result.status);
      let job;
      try { job = JSON.parse(result.body); }
      catch { throw new JobReadError('INVALID_JSON', 200); }
      if (!job || typeof job !== 'object' || Array.isArray(job)
          || typeof job.id !== 'string' || job.id.toLowerCase() !== expectedId
          || typeof job.title !== 'string' || !job.title.trim() || job.title.length > 120
          || job.status !== 'pending')
        throw new JobReadError('INVALID_JOB', 200);
      // Return only the public contract, never arbitrary additional fields.
      return { kind: 'found', job: { id: expectedId, title: job.title, status: job.status },
        attempts: result.attempts };
    },
  });
}
