import {
  type ConversationDetail,
  type CreateRunRequest,
  type RunApi,
  type RunSnapshot,
  type RunStreamHandlers,
  type RunStreamResult
} from "../../api/types";
import type { PendingRun, PendingStore } from "./useRunController";

export const snapshot = (overrides: Partial<RunSnapshot> = {}): RunSnapshot => ({
  runId: "run-1", conversationId: "conv-1", userMessageId: "u1", assistantMessageId: "a1", status: "running",
  statusReason: null, attempt: 1, error: null, outputChars: 0, hasAssistantMessage: false,
  providerCallStarted: false, admittedAt: null, endedAt: null, ...overrides
});

export class MemoryPendingStore implements PendingStore {
  items = new Map<string, PendingRun>();
  load(conversationId: string) { return this.items.get(conversationId) ?? null; }
  save(pending: PendingRun) { this.items.set(pending.conversationId, pending); }
  clear(conversationId: string) { this.items.delete(conversationId); }
}

type Script = (request: CreateRunRequest, handlers: RunStreamHandlers, signal: AbortSignal) => Promise<RunStreamResult>;

/** A scripted API: tests decide what the stream does and what the server says when asked. */
export class FakeRunApi implements RunApi {
  createCalls: CreateRunRequest[] = [];
  stopCalls: string[] = [];
  getRunCalls = 0;
  script: Script = async () => ({ started: false, done: false });
  onGetRun: (n: number) => RunSnapshot | null = () => snapshot({ status: "completed" });
  onStop: () => RunSnapshot | null = () => snapshot();
  conversation: ConversationDetail | null = null;

  createRun(request: CreateRunRequest, handlers: RunStreamHandlers, signal: AbortSignal) {
    this.createCalls.push(request);
    return this.script(request, handlers, signal);
  }
  async stopRun(runId: string) { this.stopCalls.push(runId); return this.onStop(); }
  async getRun() { this.getRunCalls += 1; return this.onGetRun(this.getRunCalls); }
  async getConversation() { return this.conversation; }
  async listConversations() { return []; }
}

// A macrotask, not a microtask: events scheduled earlier by the fake server must get to run first.
export const instantSleep = () => new Promise<void>(resolve => setTimeout(resolve, 0));
