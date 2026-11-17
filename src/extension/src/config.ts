// Настройки моста. В M1 переедут в options-страницу (chrome.storage).
export const BRIDGE_PORT = 8765;
export const BRIDGE_TOKEN = 'DevChat-dev-token';
export const BRIDGE_URL = `ws://127.0.0.1:${BRIDGE_PORT}/?token=${BRIDGE_TOKEN}`;

export const DEEPSEEK_URL = 'https://chat.deepseek.com/';
export const DEEPSEEK_TAB_QUERY = 'https://chat.deepseek.com/*';

export const CHATGPT_URL = 'https://chatgpt.com/';
export const CHATGPT_TAB_QUERY = 'https://chatgpt.com/*';
