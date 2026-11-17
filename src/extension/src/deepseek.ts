// Провайдер DeepSeek (chat.deepseek.com).
import type { CrxApplication, Page } from 'playwright-crx';
import { DEEPSEEK_TAB_QUERY, DEEPSEEK_URL } from './config';
import { S } from './selectors';
import { BridgeError, dumpClasses, dumpSendState, ensurePage, firstVisible, waitSent, wakeAgentTab, waitForStableText } from './common';
import type { BridgeResponsePayload } from './protocol';

async function ensureDeepSeek(app: CrxApplication, agent?: string): Promise<Page> {
  return ensurePage(app, DEEPSEEK_TAB_QUERY, DEEPSEEK_URL, agent);
}

export async function openChat(app: CrxApplication, args: { newChat?: boolean; agent?: string }): Promise<BridgeResponsePayload> {
  let page = await ensureDeepSeek(app, args.agent);
  if (args.newChat) await page.goto(DEEPSEEK_URL).catch(() => { /* уже там */ });
  await page.waitForLoadState('domcontentloaded');
  return { type: 'result', data: { url: page.url() } };
}

export async function getStatus(app: CrxApplication): Promise<BridgeResponsePayload> {
  const tabs = await chrome.tabs.query({ url: DEEPSEEK_TAB_QUERY });
  let composer = false, login = false;
  if (tabs.length > 0) {
    const page = await app.attach(tabs[0].id!);
    composer = (await firstVisible(page, S.composer, 3_000)) !== null;
    login = !composer && (await firstVisible(page, S.loginMarker, 300)) !== null;
  }
  return { type: 'result', data: { tabOpen: tabs.length > 0, loggedIn: composer, loginPage: login } };
}

export async function probe(app: CrxApplication): Promise<BridgeResponsePayload> {
  const page = await ensureDeepSeek(app);
  const data: Record<string, unknown> = { url: page.url() };
  for (const [name, list] of Object.entries(S)) {
    data[name] = [];
    for (const sel of list) {
      const count = await page.locator(sel).count();
      if (count > 0) (data[name] as unknown[]).push({ sel, count });
    }
  }
  return { type: 'result', data };
}

export async function dumpDom(app: CrxApplication): Promise<BridgeResponsePayload> {
  const page = await ensureDeepSeek(app);
  const raw = await page.evaluate(() => {
    const info = (el: Element) => ({
      tag: el.tagName.toLowerCase(),
      id: el.id || undefined,
      cls: ((el as HTMLElement).className?.toString() ?? '').slice(0, 120) || undefined,
      aria: el.getAttribute('aria-label') || undefined,
      placeholder: el.getAttribute('placeholder') || undefined,
      role: el.getAttribute('role') || undefined,
      testid: el.getAttribute('data-testid') || undefined,
    });
    const freq = new Map<string, number>();
    for (const el of Array.from(document.querySelectorAll('[class]'))) {
      for (const c of ((el as HTMLElement).className).toString().split(/\s+/)) {
        if (c && !/^[0-9a-f]{6,}$/.test(c)) freq.set(c, (freq.get(c) ?? 0) + 1);
      }
    }
    const topClasses = Array.from(freq.entries())
      .filter(([c]) => /^ds-/.test(c) || /message|bubble|answer|response|session|chat/i.test(c))
      .sort((a, b) => b[1] - a[1]).slice(0, 40)
      .map(([cls, n]) => ({ cls, n }));
    const bigText = Array.from(document.querySelectorAll('div'))
      .filter(el => {
        const own = Array.from(el.childNodes).filter(n => n.nodeType === 3).map(n => n.textContent ?? '').join('');
        return own.length > 100;
      })
      .slice(0, 12)
      .map(el => ({ ...info(el), len: (el as HTMLElement).innerText.length }));
    return { url: location.href, textareas: Array.from(document.querySelectorAll('textarea')).map(info), editables: Array.from(document.querySelectorAll('[contenteditable="true"]')).map(info), topClasses, bigText };
  });
  return { type: 'result', data: raw as Record<string, unknown> };
}

export async function cancel(app: CrxApplication): Promise<BridgeResponsePayload> {
  const tabs = await chrome.tabs.query({ url: DEEPSEEK_TAB_QUERY });
  if (tabs.length === 0) return { type: 'result', data: { cancelled: false, reason: 'no tab' } };
  const page = await app.attach(tabs[0].id!);
  const sel = await firstVisible(page, S.stopButton, 300);
  if (!sel) return { type: 'result', data: { cancelled: false, reason: 'не генерирует' } };
  await page.locator(sel).first().click();
  return { type: 'result', data: { cancelled: true } };
}

export async function sendPrompt(app: CrxApplication, text: string, onDelta: (chunk: string) => void, agent?: string, onPhase?: (phase: string) => void): Promise<BridgeResponsePayload> {
  if (!text.trim()) throw new BridgeError('badRequest', 'пустой промпт');
  let page = await ensureDeepSeek(app, agent);
  await wakeAgentTab(agent, DEEPSEEK_TAB_QUERY, DEEPSEEK_URL);

  // холодная загрузка вкладки: SPA рисует интерфейс после domcontentloaded — даём до 8с
  const composerSel = await firstVisible(page, S.composer, 8_000);
  if (!composerSel) throw new BridgeError('notLoggedIn', 'поле ввода DeepSeek не найдено — проверь вход или селекторы (/probe)');

  const msgSel = S.assistantMsg[0];
  const before = await page.locator(msgSel).count();

  // вставка с проверкой
  const composer = page.locator(composerSel).first();
  const filled = async (): Promise<boolean> => {
    const raw = await page.evaluate((sel: string): boolean => {
      const el = document.querySelector(sel) as HTMLTextAreaElement | null;
      return !!el && ((el.value ?? '').length > 0 || ((el as HTMLElement).textContent ?? '').length > 0);
    }, composerSel).catch(() => false);
    return raw === true;
  };
  for (let attempt = 0; attempt < 3 && !(await filled()); attempt++) {
    await composer.fill(text).catch(() => { /* повторим */ });
    await page.waitForTimeout(400);
  }
  if (!(await filled())) throw new BridgeError('composerFailed', 'не удалось вставить текст в поле ввода DeepSeek');

  // отправка: клик по кнопке, фолбэк — Enter, с дожатием
  const btn = page.locator(S.sendButton[0]).first();
  const btnState = await btn.getAttribute('aria-disabled').catch(() => 'none');
  const clicked = btnState !== 'true';
  if (clicked) await btn.click({ timeout: 3_000 }).catch(() => { /* попробуем Enter */ });
  await page.waitForTimeout(700);
  const leftover = async (): Promise<boolean> => {
    const raw = await page.evaluate((sel: string): boolean => {
      const el = document.querySelector(sel) as HTMLTextAreaElement | null;
      return !!el && ((el.value ?? '').length > 0);
    }, composerSel).catch(() => false);
    return raw === true;
  };
  if (!clicked || await leftover()) await composer.press('Enter');
  if (await leftover()) {
    await page.waitForTimeout(2_500);
    if (await leftover()) {
      await btn.click({ timeout: 3_000 }).catch(() => { /* фолбэк ниже */ });
      await page.waitForTimeout(700);
      if (await leftover()) await composer.press('Enter');
    }
  }

  // фаза «отправлено»
  if (!(await waitSent(page, text, composerSel))) throw new BridgeError('sendFailed', 'промт не появился в чате — отправка не сработала');
  onPhase?.('sent');

  const session = () => ensureDeepSeek(app, agent);
  const markdown = await waitForStableText(page, msgSel, before, onDelta, composerSel, true, session);
  return { type: 'result', markdown, data: { url: page.url() } };
}
