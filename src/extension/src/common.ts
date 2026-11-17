// Общие хелперы провайдеров (DeepSeek, ChatGPT): ошибки, селекторы, вкладки, ожидание ответа.
import type { CrxApplication, Page } from 'playwright-crx';

export class BridgeError extends Error {
  constructor(public code: string, message: string, public data?: Record<string, unknown>) { super(message); }
}

export async function firstVisible(page: Page, selectors: string[], timeoutMs = 500): Promise<string | null> {
  // поллинг до дедлайна: isVisible() мгновенна и таймаут не ждёт, а SPA рисует интерфейс с задержкой
  const deadline = Date.now() + timeoutMs;
  do {
    for (const sel of selectors) {
      try {
        if (await page.locator(sel).first().isVisible()) return sel;
      } catch { /* пробуем следующий фолбэк */ }
    }
    await page.waitForTimeout(300);
  } while (Date.now() < deadline);
  return null;
}

// вкладки агентов: у каждого агента оркестратора своя вкладка со своим контекстом
const agentTabs = new Map<string, number>();

export function agentTabIdOf(agent: string): number | undefined {
  return agentTabs.get(agent);
}

/** Сбросить привязку вкладки агента — следующий ход начнётся в чистом контексте. */
export function resetAgentTab(agent: string): void {
  const id = agentTabs.get(agent);
  agentTabs.delete(agent);
  if (typeof id === 'number') void chrome.tabs.remove(id).catch(() => { /* уже закрыта */ });
}

export function mainTabIdOf(tabQuery: string): number | undefined {
  return mainTabs.get(tabQuery);
}

// закреплённая вкладка провайдера: сначала берём существующую (например, вкладку юзера с логином),
// дальше всегда работаем с ней — никакой каши из нескольких вкладок с разными версиями UI
const mainTabs = new Map<string, number>();

async function findOrCreateTab(tabQuery: string, url: string): Promise<number> {
  const existing = mainTabs.get(tabQuery);
  if (typeof existing === 'number') {
    try {
      const t = await chrome.tabs.get(existing);
      if ((t.url ?? '').startsWith(url)) return existing;
    } catch { /* вкладка закрыта — переведязываем ниже */ }
  }
  const tabs = await chrome.tabs.query({ url: tabQuery });
  let tabId = tabs[0]?.id;
  if (typeof tabId !== 'number') {
    const tab = await chrome.tabs.create({ url, active: false });
    tabId = tab.id;
  }
  if (typeof tabId !== 'number') throw new BridgeError('internal', 'не удалось открыть вкладку провайдера');
  mainTabs.set(tabQuery, tabId);
  return tabId;
}

export async function tabForAgent(tabQuery: string, url: string, agent?: string): Promise<number> {
  if (!agent) return findOrCreateTab(tabQuery, url);
  const existing = agentTabIdOf(agent);
  if (typeof existing === 'number') {
    try { await chrome.tabs.get(existing); return existing; } catch { /* вкладка закрыта */ }
  }
  const tab = await chrome.tabs.create({ url, active: false });
  if (typeof tab.id !== 'number') throw new BridgeError('internal', 'не удалось открыть вкладку агента');
  agentTabs.set(agent, tab.id);
  return tab.id;
}

export async function ensurePage(app: CrxApplication, tabQuery: string, url: string, agent?: string): Promise<Page> {
  const tabId = await tabForAgent(tabQuery, url, agent);
  const page = await app.attach(tabId);
  await page.waitForLoadState('domcontentloaded');
  return page;
}

/** Разбудить агентную вкладку (Chrome притормаживает фоновые) и вернуть фокус пользователю. */
export async function wakeAgentTab(agent: string | undefined, tabQuery: string, url: string): Promise<void> {
  if (!agent) return;
  try {
    const tabId = agentTabIdOf(agent);
    if (typeof tabId === 'number') {
      const [active] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      await chrome.tabs.update(tabId, { active: true });
      if (active && active.id !== undefined && active.id !== tabId)
        await chrome.tabs.update(active.id, { active: true });
    }
  } catch { /* пробуждение не критично */ }
}

/**
 * Ожидание стабильного текста ответа. sentConfirmed — запрос принят (можно долго ждать первый токен);
 * getSession позволяет переприкрепиться при потере chrome.debugger. Возвращает итоговый текст.
 */
export async function waitForStableText(
  page: Page, msgSel: string, fromIndex: number,
  onDelta: (chunk: string) => void, composerSel: string | undefined,
  sentConfirmed: boolean, getSession?: () => Promise<Page>,
): Promise<string> {
  const STABLE_MS = 2500;
  const POLL_MS = 400;
  const IDLE_TIMEOUT_MS = 300_000;
  const TOTAL_TIMEOUT_MS = 900_000;
  const GIVE_UP_MS = 30_000;
  const FIRST_TOKEN_TIMEOUT_MS = 150_000;
  const started = Date.now();
  let last = '';
  let lastChange = Date.now();
  let sawAny = false;

  let cur = page;
  let evalErrors = 0;
  let limitTick = 0;
  while (Date.now() - started < TOTAL_TIMEOUT_MS) {
    let current = '';
    try {
      // диалог может жить в iframe — собираем текст по всем фреймам, берём самый длинный
      current = await collectFramesText(cur, msgSel, fromIndex);
      evalErrors = 0;
    } catch {
      if (++evalErrors >= 3) {
        evalErrors = 0;
        if (getSession) { cur = await getSession().catch(() => cur); }
        else throw new BridgeError('pageGone', 'потеряно подключение к вкладке провайдера');
      }
      await cur.waitForTimeout(500);
      continue;
    }

    if (current.length > 0) {
      sawAny = true;
      if (current !== last) {
        if (current.startsWith(last)) {
          const chunk = current.slice(last.length);
          if (chunk) onDelta(chunk);
        }
        last = current;
        lastChange = Date.now();
      } else if (sawAny && Date.now() - lastChange > STABLE_MS) {
        return last;
      }
    } else if (!sawAny && !sentConfirmed && Date.now() - started > GIVE_UP_MS) {
      const classes = await dumpClasses(cur);
      const sendObj = await dumpSendState(cur, composerSel);
      throw new BridgeError('selectorNotFound', `блок ответа по селектору «${msgSel}» не появился за 30с`, { ...(classes as Record<string, unknown>), ...sendObj });
    }

    if (sawAny && Date.now() - lastChange > IDLE_TIMEOUT_MS) break;

    // детект лимита: сообщение провайдера появляется сразу — не ждём 150с
    if (!sawAny && Date.now() - started > 15_000 && (limitTick++ % 10 === 0)) {
      const bodyRaw = await cur.evaluate((): string => (document.body.innerText || '').slice(0, 800)).catch(() => '');
      if (typeof bodyRaw === 'string' && /usage limit|server is busy|hit the (usage|session) limit|вы достигли лимита/i.test(bodyRaw)) {
        const lm = /reset on [^.]*?(\d{1,2}:\d{2})|reset in ([^.\n]{2,20})/i.exec(bodyRaw);
        throw new BridgeError('rateLimited',
          `Провайдер: лимит использования${lm ? `, ${lm[0].replace(/reset (on|in) /i, 'сброс ').slice(0, 40)}` : ''} — дождись сброса и нажми «продолжить»`);
      }
    }

    if (sentConfirmed && !sawAny && Date.now() - started > FIRST_TOKEN_TIMEOUT_MS) {
      const classes = await dumpClasses(cur);
      throw new BridgeError('timeout', 'запрос принят, но ответ не начался за 150с — провайдер перегружен или завис', classes as Record<string, unknown>);
    }
    await cur.waitForTimeout(POLL_MS);
  }
  if (last) return last;
  throw new BridgeError('timeout', 'ответ не получен за отведённое время');
}

/**
 * Снимок текста диалога: склейка всех текстовых узлов вне сайдбара (nav) —
 * не зависит от классов контейнеров (Plus-вёрстка их регулярно меняет).
 */
export async function grabThreadText(page: Page): Promise<string | null> {
  const raw = await page.evaluate((): string => {
    let acc = '';
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode: (n: Node): number => {
        let el = (n as Text).parentElement;
        while (el) {
          if (el.tagName === 'NAV') return NodeFilter.FILTER_REJECT;
          el = el.parentElement;
        }
        return NodeFilter.FILTER_ACCEPT;
      },
    });
    let node: Node | null;
    while ((node = walker.nextNode()) && acc.length < 400_000)
      acc += (node as Text).textContent + ' ';
    return acc;
  }).catch(() => null);
  return typeof raw === 'string' ? raw : null;
}

/**
 * Чтение ответа без селекторов сообщений (Plus-UI 2026): следим за текстом контейнера
 * диалога. Ответ = прирост после пользовательского сообщения. prevText — снимок,
 * сделанный ДО отправки (иначе быстрые ответы теряют начало).
 */
export async function waitForThreadAnswer(
  page: Page, userText: string, onDelta: (chunk: string) => void,
  sentConfirmed: boolean, getSession?: () => Promise<Page>, prevText?: string,
  onDiag?: (s: string) => void,
): Promise<string> {
  const STABLE_MS = 2500;
  const TOTAL_MS = 900_000;
  const IDLE_MS = 300_000;
  const started = Date.now();
  const norm = (s: string): string => s.replace(/\s+/g, ' ');
  const userNorm = norm(userText).trim();

  const grab = grabThreadText; // склейка текста вне сайдбара (nav)

  let cur = page;
  let answer = '';
  let lastChange = Date.now();
  let limitTick = 0;

  // ChatGPT вставляет сообщения в СЕРЕДИНУ DOM (после идут aria-подсказки и заголовки),
  // поэтому префикс-прирост не работает: ответ ищем ПОСЛЕ вхождения
  // пользовательского текста в полной склейке. Лента может менять регистр — ищем без учёта.
  // якорь короткий: ChatGPT сворачивает длинные промты в превью (~30-50 симв).
  // Берём ПОСЛЕДНЕЕ вхождение: свежайшее сообщение, и нечувствительно к «истории» в prev.
  const probe = userNorm.slice(0, Math.min(userNorm.length, 20));
  const probeLower = probe.toLowerCase();

  while (Date.now() - started < TOTAL_MS) {
    const text = await grab(cur).catch(() => null);
    if (text !== null && limitTick++ % 8 === 0)
      onDiag?.(`LEN${text.length}`);
    if (text === null) {
      if (getSession) { cur = await getSession().catch(() => cur); }
      else throw new BridgeError('pageGone', 'потеряно подключение к вкладке');
      await cur.waitForTimeout(500);
      continue;
    }

    const n = norm(text);
    const nLower = n.toLowerCase();
    const at = probe ? nLower.lastIndexOf(probeLower) : -1;
    if (at < 0 && limitTick % 8 === 0) onDiag?.(`anchor miss: «${probe.slice(0, 30)}»`);
    if (at >= 0) {
      // после якоря срезаем ВЕСЬ совпадающий юзер-текст (превью может быть развёрнуто
      // в полный промт — иначе хвост собственного промпта попадает в «ответ»)
      const full = userNorm.slice(0, Math.min(userNorm.length, 400));
      let matched = probe.length;
      const lim = Math.min(full.length, n.length - at);
      while (matched < lim && n[at + matched] === full[matched]) matched++;
      let after = n.slice(at + matched);

      let frag = after;
      // Plus-UI маркирует ответы: «ChatGPT сказал:» … до следующего «Вы сказали:»/дисклеймера.
      // aria-зеркало дублирует диалог — берём только первый маркированный ответ.
      const sm = /chatgpt сказал:|chatgpt said:/i.exec(after);
      if (sm) {
        const body = after.slice(sm.index + sm[0].length);
        const endM = /вы сказали:|you said:|chatgpt может допускать|chatgpt may make|to pick up a draggable|последний ответ/i.exec(body);
        frag = endM ? body.slice(0, endM.index) : body;
      }
      else {
        const noise = frag.indexOf('To pick up a draggable item');
        if (noise >= 0) frag = frag.slice(0, noise);
      }

      const candidate = frag
        .replace(/^[\s.…]*/, '')
        .replace(/^chatgpt\s+/i, '')
        .replace(/^(?:chatgpt|вы)\s+(?:сказал|сказали)[:\s]*/i, '')
        .replace(/(?:уровень\s+)?усил(?:ий|ия|ие|я)\s+рассуждения/gi, '')
        .replace(/уровень\s+усилий/gi, '')
        .replace(/thinking\s+level/gi, '')
        .replace(/(?:^|\s)(?:думаю|thinking|поиск|searching)(?=[\s.…]|$)/gi, ' ') // статус генерации
        .replace(/[^а-яa-z0-9]{0,3}(instant|fast|standard|extended|ponder|мгновенно|быстро|стандартно)(?![а-яa-z0-9])/gi, '')
        .replace(/chatgpt может допускать ошибки[\s\S]*$/i, '')
        .replace(/chatgpt may make mistakes[\s\S]*$/i, '')
        .replace(/(?:to pick up a draggable item[\s\S]*$|последний ответ\s*$)/i, '')
        .replace(/\s{2,}/g, ' ')
        .trim();
      // эхо собственного промпта из aria-копии (маркеры нашего протокола) — не ответ
      if (/cmd\.exe одной строкой|за каждое действие придёт|команда cmd\.exe одной|<result tool=/.test(candidate)) {
        await cur.waitForTimeout(700);
        continue;
      }
      // состоящий только из статусов-обрывков — не ответ
      if (candidate.length >= 2 && candidate !== answer && /[а-яa-z0-9]/i.test(candidate)) {
        if (answer.length === 0 || candidate.startsWith(answer)) {
          if (candidate.length > answer.length) onDelta(candidate.slice(answer.length));
          answer = candidate;
          lastChange = Date.now();
        }
      }
    }
    if (answer.length > 0 && Date.now() - lastChange > STABLE_MS) return answer;
    // ответ так и не начался — зависший ход, ретрай безопаснее 15-минутного ожидания
    if (answer.length === 0 && Date.now() - started > 150_000)
      throw new BridgeError('timeout', 'запрос принят, но ответ не начался за 150с');
    await cur.waitForTimeout(700);
  }
  if (answer.length > 0) return answer;
  throw new BridgeError('timeout', sentConfirmed
    ? 'запрос принят, но ответ не прочитан за отведённое время'
    : 'ответ не получен');
}

export async function dumpClasses(page: Page): Promise<Record<string, unknown>> {
  return await page.evaluate(() => {
    // глубокий обход с раскрытием Shadow DOM
    const all: Element[] = [];
    const walk = (r: Document | ShadowRoot): void => {
      for (const el of Array.from(r.querySelectorAll('*'))) all.push(el);
      for (const el of Array.from(r.querySelectorAll('*'))) {
        const sr = (el as HTMLElement).shadowRoot;
        if (sr) walk(sr);
      }
    };
    walk(document);
    const freq = new Map<string, number>();
    let shadowHosts = 0;
    for (const el of all) {
      if ((el as HTMLElement).shadowRoot) shadowHosts++;
      for (const c of ((el as HTMLElement).className ?? '').toString().split(/\s+/)) {
        if (c && !/^[0-9a-f]{6,}$/.test(c) && !c.startsWith('_') && c !== '[object' && c !== 'SVGAnimatedString]') freq.set(c, (freq.get(c) ?? 0) + 1);
      }
    }
    const top = Array.from(freq.entries()).sort((a, b) => b[1] - a[1]).slice(0, 60).map(([cls, n]) => ({ cls, n }));
    const big = all.filter(el => el.tagName === 'DIV' && (el as HTMLElement).innerText && (el as HTMLElement).innerText.length > 80)
      .slice(0, 12)
      .map(el => ({ cls: ((el as HTMLElement).className ?? '').toString().slice(0, 100), len: (el as HTMLElement).innerText.length, text: (el as HTMLElement).innerText.slice(0, 60) }));
    const bodyText = (document.body.innerText ?? '').replace(/\s+\n/g, '\n').slice(0, 400);
    // структура диалога: самый текстоёмкий контейнер и его дети (2 уровня) — маркеры сообщений
    const bigRoot = all.filter(el => el.tagName === 'DIV' && (el.textContent || '').length > 1000)
      .sort((a, b) => (a.textContent || '').length - (b.textContent || '').length)[0];
    const kids = bigRoot ? Array.from(bigRoot.children).slice(0, 15).map(k => ({
      tag: k.tagName, cls: ((k as HTMLElement).className ?? '').toString().slice(0, 70),
      len: (k.textContent || '').length, text: (k.textContent || '').replace(/\s+/g, ' ').slice(0, 70),
    })) : [];
    return { shadowHosts, total: all.length, topClasses: top, bigText: big, bodyText, threadKids: kids };
  }).catch(() => ({}) as Record<string, unknown>) as Record<string, unknown>;
}

export async function dumpSendState(page: Page, composerSel: string | undefined): Promise<Record<string, unknown>> {
  return await page.evaluate((sel: string) => {
    const area = (document.querySelector(sel) as HTMLTextAreaElement | null) ?? (document.querySelector('[contenteditable="true"]') as HTMLElement | null);
    const btn = document.querySelector('div.ds-button--primary, button[data-testid="send-button"]') as HTMLElement | null;
    return {
      composerLen: area ? ((area as HTMLTextAreaElement).value ?? (area as HTMLElement).textContent ?? '').length : -1,
      sendBtnClass: btn ? btn.className.slice(0, 90) : 'нет',
      sendDisabled: btn?.getAttribute('aria-disabled') ?? '—',
    };
  }, composerSel ?? 'textarea').catch(() => ({}) as Record<string, unknown>) as Record<string, unknown>;
}

/** Глубокий запрос с раскрытием Shadow DOM (ChatGPT 2026 держит диалог в теневых деревьях). */
export async function collectFramesText(page: Page, sel: string, from: number): Promise<string> {
  let best = '';
  for (const fr of page.frames()) {
    const raw = await fr.evaluate(({ s, f }: { s: string; f: number }): string => {
      const out: Element[] = [];
      const walk = (r: Document | ShadowRoot): void => {
        for (const el of Array.from(r.querySelectorAll(s))) out.push(el);
        for (const el of Array.from(r.querySelectorAll('*'))) {
          const sr = (el as HTMLElement).shadowRoot;
          if (sr) walk(sr);
        }
      };
      walk(document);
      return out.slice(f).map(el => (el as HTMLElement).innerText).join('\n\n');
    }, { s: sel, f: from }).catch(() => '');
    if (typeof raw === 'string' && raw.length > best.length) best = raw;
  }
  return best;
}

/** Фаза «отправлено»: промт найден в склеенном тексте страницы вне nav-сайдбара и вне поля ввода. */
export async function waitSent(page: Page, text: string, composerSel?: string): Promise<boolean> {
  const probe = text.replace(/\s+/g, ' ').trim().slice(0, 40);
  for (let i = 0; i < 40; i++) {
    let found = false;
    for (const fr of page.frames()) {
      const ok = await fr.evaluate(({ p, sel }: { p: string; sel?: string }): boolean => {
        const norm = (s: string | null): string => (s || '').replace(/\s+/g, ' ');
        // ещё стоит в поле ввода?
        if (sel) {
          const c = document.querySelector(sel) as (HTMLTextAreaElement & HTMLElement) | null;
          if (c && norm(c.value ?? c.textContent).includes(p)) return false;
        }
        // склеенный текст всей страницы, минус сайдбар (nav) и композер —
        // лента может резать сообщение на сколько угодно мелких узлов
        let acc = '';
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
          acceptNode: (n: Node): number => {
            let el = (n as Text).parentElement;
            while (el) {
              if (el.tagName === 'NAV') return NodeFilter.FILTER_REJECT;
              if (sel && el.matches(sel)) return NodeFilter.FILTER_REJECT;
              el = el.parentElement;
            }
            return NodeFilter.FILTER_ACCEPT;
          },
        });
        let node: Node | null;
        while ((node = walker.nextNode()) && acc.length < 300_000)
          acc += (node as Text).textContent + ' ';
        return norm(acc).includes(p);
      }, { p: probe, sel: composerSel }).catch(() => false);
      if (ok === true) { found = true; break; }
    }
    if (found) return true;
    await page.waitForTimeout(750);
  }
  return false;
}
