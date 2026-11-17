// Фон воркера: WS-клиент моста + роутер операций с выбором провайдера.
import { crx, type CrxApplication } from 'playwright-crx';
import { BridgeClient } from './client';
import * as deepseek from './deepseek';
import * as chatgpt from './chatgpt';
import { resetAgentTab } from './common';
import type { BridgeRequest, BridgeResponsePayload } from './protocol';

type ProviderModule = typeof deepseek;

const providers: Record<string, ProviderModule> = { deepseek, chatgpt };

function providerOf(req: BridgeRequest): ProviderModule {
  const name = typeof req.provider === 'string' && providers[req.provider] ? req.provider : 'deepseek';
  return providers[name];
}

const client = new BridgeClient();

let app: CrxApplication | null = null;
async function ensureApp(): Promise<CrxApplication> {
  if (!app) app = await crx.start();
  return app;
}

client.handler = async (req): Promise<BridgeResponsePayload> => {
  const started = Date.now();
  client.sendEvent('op', `${req.provider ?? 'deepseek'}/${req.op} ← старт`);
  try {
    const res = await handleOp(req);
    client.sendEvent('op', `${req.provider ?? 'deepseek'}/${req.op} ✓ за ${Date.now() - started}мс`);
    return res;
  } catch (e) {
    client.sendEvent('op', `${req.provider ?? 'deepseek'}/${req.op} ✗ ${String((e as Error).message).slice(0, 200)}`);
    throw e;
  }
};

async function handleOp(req: BridgeRequest): Promise<BridgeResponsePayload> {
  const p = providerOf(req);
  switch (req.op) {
    case 'ping':
      return { type: 'pong' };
    case 'getStatus':
      return await p.getStatus(await ensureApp());
    case 'probe':
      return await p.probe(await ensureApp());
    case 'dump':
      return await p.dumpDom(await ensureApp());
    case 'openChat':
      return await p.openChat(await ensureApp(), {
        newChat: req.newChat === true,
        agent: typeof req.agent === 'string' ? req.agent : undefined,
      });
    case 'cancel':
      return await p.cancel(await ensureApp());
    case 'resetAgent':
      if (typeof req.agent === 'string') resetAgentTab(req.agent);
      return { type: 'result', data: { reset: true } };
    case 'reloadExtension':
      // горячая перезагрузка кода расширения (SW вечно живёт при активном WS и не подхватывает dist)
      client.sendEvent('op', 'reloadExtension — перезапуск расширения');
      setTimeout(() => chrome.runtime.reload(), 500);
      return { type: 'result', data: { reloading: true } };
    case 'sendPrompt': {
      const text = typeof req.text === 'string' ? req.text : '';
      const agent = typeof req.agent === 'string' ? req.agent : undefined;
      return await p.sendPrompt(await ensureApp(), text,
        (chunk) => client.sendDelta(req.id, chunk), agent,
        (phase) => client.sendEvent('phase', phase));
    }
    default:
      return { type: 'error', code: 'unknownOp', message: `неизвестная операция: ${req.op}` };
  }
}

// подключаемся при любом (пере)запуске service worker
client.start();
chrome.runtime.onInstalled.addListener(() => client.start());
chrome.runtime.onStartup.addListener(() => client.start());

// страховка от засыпания SW, когда приложение не запущено и WS не держит воркер живым
chrome.alarms.create('DevChat-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => client.start());
