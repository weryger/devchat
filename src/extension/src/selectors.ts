// ВСЕ селекторы DeepSeek — только здесь. Списки = фолбэки (первый сработавший побеждает).
// Статус: предварительные, сверить с живым DOM через /probe.
export const S: Record<string, string[]> = {
  // поле ввода промпта (на странице единственный textarea, placeholder локализован)
  composer: [
    'textarea[placeholder]',
    'textarea',
    'div[contenteditable="true"]',
  ],
  // тело сообщения ассистента (рендер markdown)
  assistantMsg: [
    'div.ds-markdown',
    'div.markdown',
  ],
  // кнопка остановки генерации (появляется во время ответа)
  stopButton: [
    'div[role="button"][aria-label*="Stop" i]',
    'button[aria-label*="Stop" i]',
    '[class*="stop" i][role="button"]',
  ],
  // кнопка копирования raw-markdown у сообщения
  copyButton: [
    'div[role="button"][aria-label*="Copy" i]',
    'button[aria-label*="Copy" i]',
  ],
  // маркер страницы входа
  loginMarker: [
    'div[role="button"]:has-text("Log in")',
    'button:has-text("Log in")',
  ],
  // кнопка отправки (круглая первичная в композере; при пустом вводе disabled)
  sendButton: [
    'div.ds-button--primary.ds-button--circle[role="button"]',
    'div.ds-button--primary[role="button"]',
  ],
};

export function firstOf(list: string[]): string {
  return list[0];
}
