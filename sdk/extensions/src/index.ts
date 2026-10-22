import { maximumFrameBytes, protocolVersion, type Initialize, type JsonObject, type View, type ViewRequest,
  type ObserverEvent, type PreHookInput, type PostHookEvent, type PreHookDecision, type ToolInvocation,
  type ActivityPage, type CurrentSession } from './contracts.js';
import { setTimeout as delay } from 'node:timers/promises';
export * from './contracts.js';
type Handler = (parameters: JsonObject, signal: AbortSignal) => unknown | Promise<unknown>;
type ToolHandler = (parameters: JsonObject, signal: AbortSignal, invocation: ToolInvocation) => unknown | Promise<unknown>;

/** Trusted process helper, NOT a sandbox. No credentials/Engine/DI/SQLite API. */
export class Extension {
  private identity?: Initialize;
  private nextId = 0;
  private readonly pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }>();
  private readonly handlers = new Map<string, Handler>();
  private readonly tools = new Map<string, ToolHandler>();
  private readonly hooks = new Map<string, Handler>();
  private readonly active = new Map<string, AbortController>();
  private buffer = Buffer.alloc(0);
  private readonly cleanups: (() => void)[] = [];
  private readonly lifetime = new AbortController();

  constructor() {
    const major = Number(process.versions.node.split('.')[0]);
    if (major < 22 || !process.release.lts) throw new Error('node_lts_required');
    this.handle('tools/call', (params, signal) => {
      const handler = this.tools.get(String(params.name));
      if (!handler) throw new Error('tool_not_registered');
      const context = params._context as JsonObject | undefined;
      return handler((params.arguments ?? {}) as JsonObject, signal, { toolCallId: typeof params.toolCallId === 'string' ? params.toolCallId : undefined, origin: this.id,
        sessionId: typeof context?.sessionId === 'string' ? context.sessionId : null,
        correlationId: typeof context?.correlationId === 'string' ? context.correlationId : null });
    });
    this.handle('hooks/execute', (params, signal) => {
      const handler = this.hooks.get(String(params.point)); if (!handler) throw new Error('hook_not_registered');
      return handler(params, signal);
    });
  }
  get id(): string { if (!this.identity) throw new Error('not_initialized'); return this.identity.extensionId; }
  handle(method: string, handler: Handler): void {
    if (this.handlers.has(method) || ['initialize', 'shutdown'].includes(method)) throw new Error('duplicate_or_reserved_handler');
    this.handlers.set(method, handler);
  }
  tool(name: string, handler: ToolHandler): void { if (this.tools.has(name)) throw new Error('duplicate_tool'); this.tools.set(name, handler); }
  observe(point: string, handler: (event: ObserverEvent, signal: AbortSignal) => void | Promise<void>): void {
    this.registerHook(point, async (input, signal) => { await handler(input as unknown as ObserverEvent, signal); return { kind: 'continue' }; });
  }
  before(point: string, handler: (input: PreHookInput, signal: AbortSignal) => PreHookDecision | Promise<PreHookDecision>): void {
    if (!point.startsWith('Before')) throw new Error('pre_hook_point_required');
    this.registerHook(point, (input, signal) => handler(input as unknown as PreHookInput, signal));
  }
  after(point: string, handler: (event: PostHookEvent, signal: AbortSignal) => void | Promise<void>): void {
    if (!point.startsWith('After') && !['ToolFailed', 'RunCompleted', 'RunFailed'].includes(point)) throw new Error('post_hook_point_required');
    this.registerHook(point, async (input, signal) => { await handler(input as unknown as PostHookEvent, signal); return { kind: 'continue' }; });
  }
  private registerHook(point: string, handler: Handler): void {
    if (this.hooks.has(point)) throw new Error('duplicate_hook'); this.hooks.set(point, handler);
  }
  view(handler: (request: ViewRequest, signal: AbortSignal) => View | Promise<View>): void {
    this.handle('view.read', (parameters, signal) => handler(parameters as unknown as ViewRequest, signal));
  }
  onDispose(cleanup: () => void): void { this.cleanups.push(cleanup); }
  log(level: 'info' | 'warning' | 'error', code: string): void {
    if (!/^[a-zA-Z0-9._-]{1,128}$/.test(code)) throw new Error('log_code_invalid');
    process.stderr.write(JSON.stringify({ level, code }) + '\n');
  }
  async service(method: string, parameters: JsonObject = {}): Promise<unknown> {
    if (!this.identity || this.pending.size >= 4) throw new Error('not_initialized_or_concurrency_limit');
    const id = 'ext-' + ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('core_service_timeout')); }, 15000);
      this.pending.set(id, { resolve, reject, timer });
      try { this.write({ jsonrpc: '2.0', id, method, params: parameters }); }
      catch (error) { clearTimeout(timer); this.pending.delete(id); reject(error); }
    });
  }
  workspace = { directory: (path = '') => this.service('workspace.directory', { path }), search: (search: string) => this.service('workspace.search', { search }), file: (path: string) => this.service('workspace.file', { path }) };
  git = { status: () => this.service('git.status'), diff: (path: string) => this.service('git.diff', { path }) };
  sessions = { agents: () => this.service('sessions.agents'), current: () => this.service('sessions.current') as Promise<CurrentSession | null> };
  history = { timeline: () => this.service('history.timeline') };
  activity = {
    read: (afterSequence = 0, limit = 64) => this.service('activity.read', { afterSequence, limit }) as Promise<ActivityPage>,
    watch: (options: { afterSequence?: number; intervalMs?: number; signal?: AbortSignal } = {}) => this.watchActivity(options),
  };
  /** Bounded pull/replay, not push. No queue: the next page is read only when the consumer resumes. */
  private async *watchActivity(options: { afterSequence?: number; intervalMs?: number; signal?: AbortSignal }): AsyncGenerator<ActivityPage> {
    const signal = options.signal ? AbortSignal.any([this.lifetime.signal, options.signal]) : this.lifetime.signal;
    let cursor = options.afterSequence ?? 0;
    const interval = options.intervalMs ?? 2000;
    if (!Number.isSafeInteger(cursor) || cursor < 0 || !Number.isFinite(interval) || interval < 500 || interval > 60000)
      throw new Error('activity_options_invalid');
    while (!signal.aborted) {
      const page = await this.activity.read(cursor);
      if (signal.aborted) break;
      if (page.sessionId !== this.identity?.sessionId || !Number.isSafeInteger(page.nextSequence)
        || page.nextSequence < cursor || page.highWater < page.nextSequence || !Array.isArray(page.items) || page.items.length > 128)
        throw new Error('activity_scope_or_cursor_invalid');
      yield page;
      cursor = page.nextSequence;
      if (cursor < page.highWater) continue;
      try { await delay(interval, undefined, { signal }); }
      catch (error) { if (signal.aborted) break; throw error; }
    }
  }
  config = { get: (key: string, scope = 'workspace') => this.service('config.get', { key, scope }), set: (key: string, value: unknown, scope = 'workspace') => this.service('config.set', { key, value, scope }) };
  /** UI-only literal notification. Never contributes model context or grants authority. */
  presentation = { notify: (id: string, level: 'info' | 'warning' | 'error', text: string) => this.service('presentation.notify', { id, level, text }) };
  listen(): void {
    process.stdin.on('data', (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      let end: number;
      while ((end = this.buffer.indexOf(10)) !== -1) {
        if (end > maximumFrameBytes) return this.fail();
        const line = this.buffer.subarray(0, end); this.buffer = this.buffer.subarray(end + 1);
        try { void this.receive(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(line))); }
        catch { return this.fail(); }
      }
      if (this.buffer.length > maximumFrameBytes) this.fail();
    });
    process.stdin.on('end', () => this.dispose());
  }
  private async receive(message: JsonObject): Promise<void> {
    if (message.jsonrpc === '2.0' && message.method === '$/cancelRequest') {
      const id = (message.params as JsonObject)?.id;
      if (typeof id === 'string') this.active.get(id)?.abort(); return;
    }
    if (message.jsonrpc !== '2.0' || typeof message.id !== 'string') return this.fail();
    const id = message.id;
    if (typeof message.method !== 'string') {
      const waiting = this.pending.get(id); if (!waiting) return this.fail();
      clearTimeout(waiting.timer); this.pending.delete(id);
      if (message.error) waiting.reject(new Error('core_service_denied')); else waiting.resolve(message.result);
      return;
    }
    if (this.active.size >= 4 || this.active.has(id)) return this.fail();
    const controller = new AbortController(); this.active.set(id, controller);
    try {
      let result: unknown;
      const parameters = message.params as JsonObject;
      if (message.method === 'initialize') {
        const init = parameters as unknown as Initialize;
        if (this.identity || init.protocolVersion !== protocolVersion || !Array.isArray(init.capabilities)) throw new Error('incompatible_protocol');
        this.identity = init; result = init;
      } else if (!this.identity) throw new Error('initialize_required');
      else if (message.method === 'shutdown') { result = {}; this.dispose(); }
      else {
        const handler = this.handlers.get(message.method); if (!handler) throw new Error('method_not_found');
        result = await handler(parameters, controller.signal);
      }
      this.write({ jsonrpc: '2.0', id, result: result ?? null });
    } catch { this.write({ jsonrpc: '2.0', id, error: { code: -32001, message: 'extension_request_failed' } }); }
    finally { this.active.delete(id); }
  }
  private write(value: unknown): void {
    const json = JSON.stringify(value); if (Buffer.byteLength(json) > maximumFrameBytes) throw new Error('frame_limit');
    if (!process.stdout.write(json + '\n') && process.stdout.writableLength > maximumFrameBytes * 4) this.fail();
  }
  private dispose(): void {
    this.lifetime.abort();
    for (const controller of this.active.values()) controller.abort();
    for (const cleanup of this.cleanups.splice(0)) { try { cleanup(); } catch { this.log('warning', 'cleanup_failed'); } }
    for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(new Error('extension_stopped')); }
    this.pending.clear();
  }
  private fail(): void { this.dispose(); process.exitCode = 1; process.stdin.destroy(); }
}
