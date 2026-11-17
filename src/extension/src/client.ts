// WS-клиент моста: подключается к C#-приложению, переподключается с backoff.
import { BRIDGE_URL } from './config';
import type { BridgeRequest, BridgeResponsePayload } from './protocol';

const log = (m: string) => console.log(`[bridge] ${m}`);

export class BridgeClient {
  private ws: WebSocket | null = null;
  private tries = 0;
  private stopped = false;

  /** Обработчик запросов от приложения; возвращает payload ответа. */
  handler?: (req: BridgeRequest) => Promise<BridgeResponsePayload>;

  get connected(): boolean {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  start(): void {
    this.stopped = false;
    if (!this.connected) this.connect();
  }

  private connect(): void {
    if (this.connected) return;
    log(`подключение к ${BRIDGE_URL.replace(/token=[^&]+/, 'token=***')}`);
    const ws = new WebSocket(BRIDGE_URL);
    this.ws = ws;
    ws.onopen = () => {
      this.tries = 0;
      log('подключено к приложению');
      this.sendEvent('connected', ` попытка №${this.tries + 1}`);
    };
    ws.onmessage = (ev) => void this.onMessage(String(ev.data));
    ws.onclose = () => {
      if (this.ws === ws) this.ws = null;
      this.sendEvent('disconnected', 'ws закрыт');
      if (this.stopped) return;
      const delay = Math.min(30_000, 1000 * 2 ** this.tries++);
      log(`соединение потеряно, повтор через ${Math.round(delay / 1000)}с`);
      setTimeout(() => this.connect(), delay);
    };
    ws.onerror = () => { /* затем последует onclose */ };
  }

  private async onMessage(raw: string): Promise<void> {
    let msg: BridgeRequest;
    try { msg = JSON.parse(raw); } catch { return; }
    if (typeof msg?.id !== 'string' || typeof msg?.op !== 'string') return;

    try {
      const payload = await this.handler?.(msg)
        ?? { type: 'error', code: 'noHandler', message: `нет обработчика для op=${msg.op}` };
      this.send({ id: msg.id, ...payload });
    } catch (e) {
      const code = (e as { code?: string }).code ?? 'internal';
      const message = e instanceof Error ? e.message : String(e);
      const data = (e as { data?: Record<string, unknown> }).data;
      this.send({ id: msg.id, type: 'error', code, message, ...(data ? { data } : {}) });
    }
  }

  send(obj: unknown): void {
    if (this.connected) this.ws!.send(JSON.stringify(obj));
  }

  /** Частичный текст ответа во время генерации. */
  sendDelta(id: string, text: string): void {
    this.send({ id, type: 'delta', text });
  }

  /** Событие без запроса (журнал приложения). */
  sendEvent(name: string, text = ''): void {
    this.send({ type: 'event', name, text });
  }
}
