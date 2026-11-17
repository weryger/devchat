// Типы протокола моста — зеркало C# стороны (docs/PLAN.md, раздел «Протокол моста»).

export interface BridgeRequest {
  id: string;
  op: string;
  text?: string;
  [key: string]: unknown;
}

export type BridgeOp = 'ping' | 'getStatus' | 'probe' | 'sendPrompt' | 'cancel' | 'openChat';

export interface BridgeResponsePayload {
  type: 'result' | 'error' | 'pong';
  markdown?: string;
  code?: string;
  message?: string;
  data?: Record<string, unknown>;
}
