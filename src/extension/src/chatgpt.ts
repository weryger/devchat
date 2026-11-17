// Провайдер ChatGPT (chatgpt.com).
import type { CrxApplication, Page } from 'playwright-crx';
import { CHATGPT_TAB_QUERY, CHATGPT_URL } from './config';
import { BridgeError, dumpClasses, ensurePage, firstVisible, grabThreadText, mainTabIdOf, waitSent, wakeAgentTab, waitForStableText, waitForThreadAnswer } from './common';
import type { BridgeResponsePayload } from './protocol';

// Селекторы под реальный DOM (дамп 2026-11-17): композер — div.ProseMirror (contenteditable),
// aria-метки локализованы, data-testid почти нет; отправка — Enter (нативно для ProseMirror)
const G = {
  composer: [
    'div.ProseMirror',
    'div[contenteditable="true"]',
    'div#prompt-textarea',
    'textarea#prompt-textarea',
    'textarea',
  ],
  assistantMsg: [
    'div[data-message-author-role="assistant"]',
    'div.JYjgF4u8',
    'div.markdown',
  ],
  sendButton: [
    'button[data-testid="send-button"]',
    'button[aria-label*="Отправ" i]',
    'button[aria-label*="Send" i]',
  ],
  stopButton: [
    'button[data-testid="stop-button"]',
    'button[aria-label*="Останов" i]',
    'button[aria-label*="Stop" i]',
  ],
  loginMarker: [
    'button:has-text("Log in")',
    'div[data-testid="login-button"]',
  ],
};

export async function ensureChatGPT(app: CrxApplication, agent?: string): Promise<Page> {
  return ensurePage(app, CHATGPT_TAB_QUERY, CHATGPT_URL, agent);
}

export async function openChat(app: CrxApplication, args: { newChat?: boolean; agent?: string }): Promise<BridgeResponsePayload> {
  let page = await ensureChatGPT(app, args.agent);
  if (args.newChat) await page.goto(CHATGPT_URL).catch(() => { /* уже там */ });
  await page.waitForLoadState('domcontentloaded');
  return { type: 'result', data: { url: page.url() } };
}

export async function getStatus(app: CrxApplication): Promise<BridgeResponsePayload> {
  const tabs = await chrome.tabs.query({ url: CHATGPT_TAB_QUERY });
  let composer = false, login = false;
  if (tabs.length > 0) {
    const page = await app.attach(tabs[0].id!);
    composer = (await firstVisible(page, G.composer, 5_000)) !== null;
    login = !composer && (await firstVisible(page, G.loginMarker, 300)) !== null;
  }
  return { type: 'result', data: { tabOpen: tabs.length > 0, loggedIn: composer, loginPage: login } };
}

export async function probe(app: CrxApplication): Promise<BridgeResponsePayload> {
  const page = await ensureChatGPT(app);
  const data: Record<string, unknown> = { url: page.url() };
  for (const [name, list] of Object.entries(G)) {
    data[name] = [];
    for (const sel of list) {
      const count = await page.locator(sel).count();
      if (count > 0) (data[name] as unknown[]).push({ sel, count });
    }
  }
  return { type: 'result', data };
}

export async function cancel(app: CrxApplication): Promise<BridgeResponsePayload> {
  const tabs = await chrome.tabs.query({ url: CHATGPT_TAB_QUERY });
  if (tabs.length === 0) return { type: 'result', data: { cancelled: false, reason: 'no tab' } };
  const page = await app.attach(tabs[0].id!);
  const sel = await firstVisible(page, G.stopButton, 300);
  if (!sel) return { type: 'result', data: { cancelled: false, reason: 'не генерирует' } };
  await page.locator(sel).first().click();
  return { type: 'result', data: { cancelled: true } };
}

export async function dumpDom(app: CrxApplication): Promise<BridgeResponsePayload> {
  const page = await ensureChatGPT(app);
  const classes = await dumpClasses(page);
  const raw = await page.evaluate(() => {
    const info = (el: Element) => ({
      tag: el.tagName.toLowerCase(),
      id: el.id || undefined,
      cls: ((el as HTMLElement).className?.toString() ?? '').slice(0, 120) || undefined,
      testid: el.getAttribute('data-testid') || undefined,
      aria: el.getAttribute('aria-label') || undefined,
    });
    return {
      url: location.href,
      articles: Array.from(document.querySelectorAll('article')).slice(0, 8).map(info),
      messages: Array.from(document.querySelectorAll('[data-message-author-role], [data-message-id]')).slice(0, 10).map(info),
      testids: Array.from(document.querySelectorAll('[data-testid]')).slice(0, 30).map(info),
      iframes: Array.from(document.querySelectorAll('iframe')).map(f => ({ src: (f as HTMLIFrameElement).src?.slice(0, 120) ?? '', cls: (f.className ?? '').toString().slice(0, 80) })),
    };
  });
  return { type: 'result', data: { ...(classes as Record<string, unknown>), ...(raw as Record<string, unknown>) } };
}

export async function sendPrompt(app: CrxApplication, text: string, onDelta: (chunk: string) => void, agent?: string, onPhase?: (phase: string) => void): Promise<BridgeResponsePayload> {
  if (!text.trim()) throw new BridgeError('badRequest', 'пустой промпт');
  let page = await ensureChatGPT(app, agent);

  // ChatGPT не рендерит диалог в фоновой вкладке — активируем её на время хода,
  // фокус пользователю вернём в finally
  let userTabId: number | undefined;
  const myTabId = mainTabIdOf(CHATGPT_TAB_QUERY);
  try {
    const [active] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    userTabId = active?.id;
    if (typeof myTabId === 'number' && myTabId !== userTabId)
      await chrome.tabs.update(myTabId, { active: true });
  } catch { /* пробуждение не критично */ }

  try {
    return await runSendPrompt(app, page, text, onDelta, agent, onPhase);
  } finally {
    // возвращаем фокус, только если пользователь всё ещё на нашей вкладке
    // (сам переключился — не перебиваем)
    if (typeof myTabId === 'number') {
      const [now] = await chrome.tabs.query({ active: true, lastFocusedWindow: true }).catch(() => [] as chrome.tabs.Tab[]);
      if (now?.id === myTabId && userTabId !== undefined && userTabId !== myTabId)
        await chrome.tabs.update(userTabId, { active: true }).catch(() => { /* окно могли закрыть */ });
    }
  }
}

async function runSendPrompt(app: CrxApplication, page: Page, text: string, onDelta: (chunk: string) => void, agent?: string, onPhase?: (phase: string) => void): Promise<BridgeResponsePayload> {

  // композер ChatGPT: при загрузке рендерится временный textarea, после гидрации — div.ProseMirror.
  // Поэтому резолвим селектор заново перед КАЖДЫМ действием, а не один раз.
  let composerSel: string = await firstVisible(page, G.composer, 10_000) ?? '';
  if (!composerSel) throw new BridgeError('notLoggedIn', 'поле ввода ChatGPT не найдено — проверь вход (chatgpt.com) или селекторы (/probe)');

  // блок ответа: CSS-список — сработает любой существующий вариант вёрстки
  // (дамп 2026-11-17: ответ рендерится абзацами div.JYjgF4u8)
  const msgSel = G.assistantMsg.join(', ');
  const before = await page.locator(G.assistantMsg[0]).count();

  const refreshComposer = async (): Promise<void> => {
    // гидрация могла заменить элемент — сверяемся с фактическим DOM
    const again = await firstVisible(page, G.composer, 800);
    if (again) composerSel = again;
  };
  // ChatGPT пересоздаёт элементы композера после ввода — локаторы пере-запрашиваем каждый раз
  const composerNow = () => page.locator(composerSel).first();
  const filled = async (): Promise<boolean> => {
    const raw = await page.evaluate((sel: string): boolean => {
      const el = document.querySelector(sel) as (HTMLTextAreaElement & HTMLElement) | null;
      if (!el) return false;
      return (el.value ?? el.textContent ?? '').length > 0;
    }, composerSel).catch(() => false);
    return raw === true;
  };
  await page.waitForTimeout(2_000); // пережидаем гидрацию (textarea → ProseMirror)
  await refreshComposer();
  onPhase?.(`composer=${composerSel}`);
  // снимок ленты ДО ВВОДА текста: композер входит в склейку вне nav,
  // и запрос из поля попал бы в prev, смещая поиск якоря на следующий ход
  const prevSnapshot = await grabThreadText(page);

  for (let attempt = 0; attempt < 3 && !(await filled()); attempt++) {
    await refreshComposer();
    await page.locator(composerSel).first().fill(text).catch(() => { /* повторим */ });
    await page.waitForTimeout(500);
    if (!(await filled())) await composerNow().click({ timeout: 2_000 }).catch(() => {});
  }
  await refreshComposer(); // перед проверкой успеха — вдруг элемент заменился после вставки
  if (!(await filled())) throw new BridgeError('composerFailed', 'не удалось вставить текст в поле ввода ChatGPT');

  // отправка: ProseMirror отправляет Enter'ом нативно — Enter первым, кнопка фолбэком.
  // Локаторы — свежие на каждую попытку (ChatGPT пересоздаёт элементы после ввода).
  const trySendButton = async (): Promise<boolean> => {
    for (const sel of G.sendButton) {
      const btn = page.locator(sel).first();
      try { await btn.waitFor({ state: 'visible', timeout: 2_000 }); }
      catch { continue; } // этого варианта кнопки нет — следующий
      const disabled = (await btn.getAttribute('disabled').catch(() => null))
        ?? (await btn.getAttribute('aria-disabled').catch(() => null));
      if (disabled === 'true' || disabled === '') continue;
      await btn.click({ timeout: 2_000 }).catch(() => { /* следующий вариант */ });
      await page.waitForTimeout(800);
      if (!(await filled())) return true;
    }
    return false;
  };

  await page.waitForTimeout(400);
  let sentOk = false;
  await refreshComposer();
  await composerNow().press('Enter', { timeout: 4_000 }).catch(() => { /* пересоздан — ниже кнопка */ });
  await page.waitForTimeout(800);
  sentOk = !(await filled());
  if (!sentOk) sentOk = await trySendButton();

  // фоллбек для React-полей, не видящих программный fill: ввод через insertText —
  // шлёт настоящие beforeinput/input события, React их принимает; затем Enter
  if (!sentOk)
  {
    await refreshComposer();
    const c = composerNow();
    await c.click({ timeout: 3_000 }).catch(() => { /* фокус не критичен */ });
    await page.keyboard.press('Control+a');
    await page.keyboard.insertText(text).catch(() => { /* последний шанс ниже */ });
    await page.waitForTimeout(600);
    await refreshComposer();
    await composerNow().press('Enter', { timeout: 4_000 }).catch(() => { /* кнопка ниже */ });
    await page.waitForTimeout(800);
    sentOk = !(await filled());
    if (!sentOk) sentOk = await trySendButton();
  }

  if (!sentOk)
  {
    await page.waitForTimeout(1_500);
    await composerNow().press('Enter', { timeout: 4_000 }).catch(() => { /* последний шанс */ });
    await page.waitForTimeout(800);
    sentOk = !(await filled());
    if (!sentOk) sentOk = await trySendButton();
  }

  if (!(await waitSent(page, text, composerSel))) throw new BridgeError('sendFailed', 'промт не появился в чате ChatGPT — отправка не сработал');
  onPhase?.('sent');

  const session = () => ensureChatGPT(app, agent);

  // Чтение ответа ChatGPT — только через прирост контейнера диалога:
  // универсально для Plus/free-вёрсток; селекторный путь даёт ложные срабатывания
  // (например, div.markdown в сайдбаре) и вечное ожидание.
  const markdown = await waitForThreadAnswer(page, text, onDelta, true, session, prevSnapshot ?? undefined,
    (s) => onPhase?.(s));
  return { type: 'result', markdown, data: { url: page.url() } };
}
