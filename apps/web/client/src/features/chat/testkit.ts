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
  async listConversations() { return { items: [], hasMore: false, nextCursor: null }; }
  async getWorkspace() { return { providers: [], defaultProviderId: "" }; }
  async getAuthStatus() { return { email: "t@example.com", safetyThresholds: [] }; }
  async getAttachment() { return new Blob(["x"]); }
  renamed: { id: string; title: string }[] = [];
  deleted: string[] = [];
  async renameConversation(id: string, title: string) { this.renamed.push({ id, title }); }
  async deleteConversation(id: string) { this.deleted.push(id); }
  async exportConversation() { return { blob: new Blob(["x"]), fileName: "c.md" }; }
  defaults: import("../../api/types").StoredSettings | null = null;
  savedDefaults: import("../../api/types").StoredSettings[] = [];
  savedConversationSettings: { id: string; settings: import("../../api/types").StoredSettings }[] = [];
  async getDefaults() { return this.defaults; }
  async putDefaults(settings: import("../../api/types").StoredSettings) { this.savedDefaults.push(settings); this.defaults = settings; return settings; }
  roles: { id: string; name: string; prompt: string }[] = [];
  async listRoles() { return { roles: this.roles, maxRoles: 30 }; }
  async createRole(role: { name: string; prompt: string }) { const created = { id: `r${this.roles.length + 1}`, ...role }; this.roles.push(created); return created; }
  async updateRole(id: string, role: { name: string; prompt: string }) { const updated = { id, ...role }; this.roles = this.roles.map(r => (r.id === id ? updated : r)); return updated; }
  async deleteRole(id: string) { this.roles = this.roles.filter(r => r.id !== id); }
  async putConversationSettings(id: string, settings: import("../../api/types").StoredSettings) { this.savedConversationSettings.push({ id, settings }); }
}

// A macrotask, not a microtask: events scheduled earlier by the fake server must get to run first.
export const instantSleep = () => new Promise<void>(resolve => setTimeout(resolve, 0));
