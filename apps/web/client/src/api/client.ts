import { readEventStream } from "./sse";
import {
  ApiError,
  NetworkError,
  type Citation,
  type AuthStatus,
  type ConversationDetail,
  type ConversationPage,
  type CreateRunRequest,
  type DeltaEvent,
  type RunApi,
  type RunSnapshot,
  type RunStreamHandlers,
  type RunStreamResult,
  type StoredSettings,
  type UserRole,
  type WorkspaceConfig
} from "./types";

export type AuthHeaders = () => Promise<Record<string, string>>;

/** The browser side of the v2 run API. The browser only ever holds a Firebase token, never a model key. */
export function createApiClient(authHeaders: AuthHeaders, fetchImpl: typeof fetch = fetch): RunApi {
  async function request(path: string, init: RequestInit = {}): Promise<Response> {
    const headers = { ...(await authHeaders()), ...(init.headers as Record<string, string> | undefined) };
    return fetchImpl(path, { ...init, headers });
  }

  async function fail(response: Response): Promise<never> {
    let code = `http_${response.status}`;
    let message = `请求失败：${response.status}`;
    let retryable = response.status >= 500;
    let extra: unknown;
    try {
      const body = (await response.json()) as { error?: { code?: string; message?: string; retryable?: boolean; extra?: unknown } };
      code = body.error?.code ?? code;
      message = body.error?.message ?? message;
      retryable = body.error?.retryable ?? retryable;
      extra = body.error?.extra;
    } catch {
      // no JSON body
    }
    throw new ApiError(response.status, code, message, retryable, extra);
  }

  return {
    async createRun(createRequest: CreateRunRequest, handlers: RunStreamHandlers, signal: AbortSignal): Promise<RunStreamResult> {
      let started = false;
      let done = false;

      let response: Response;
      try {
        response = await request("/api/v2/runs", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(createRequest),
          signal
        });
      } catch (error) {
        throw new NetworkError(error instanceof Error ? error.message : "network error", false);
      }

      if (!response.ok) await fail(response);
      if (!response.body) throw new NetworkError("empty response", false);

      try {
        await readEventStream(response.body, {
          run: data => {
            started = true;
            handlers.onRun(data as RunSnapshot);
          },
          delta: data => handlers.onDelta(data as DeltaEvent),
          citations: data => handlers.onCitations((data as { items: Citation[] }).items),
          done: data => {
            done = true;
            handlers.onDone(data as RunSnapshot);
          }
        });
      } catch (error) {
        if (signal.aborted) return { started, done };
        throw new NetworkError(error instanceof Error ? error.message : "stream error", started);
      }

      return { started, done };
    },

    async stopRun(runId: string): Promise<RunSnapshot | null> {
      const response = await request(`/api/v2/runs/${runId}/stop`, { method: "POST" });
      if (response.status === 404) return null;
      if (!response.ok) await fail(response);
      return (await response.json()) as RunSnapshot;
    },

    async getRun(runId: string): Promise<RunSnapshot | null> {
      const response = await request(`/api/v2/runs/${runId}`);
      if (response.status === 404) return null;
      if (!response.ok) await fail(response);
      return (await response.json()) as RunSnapshot;
    },

    async getConversation(conversationId: string): Promise<ConversationDetail | null> {
      const response = await request(`/api/v2/conversations/${conversationId}`);
      if (response.status === 404) return null;
      if (!response.ok) await fail(response);
      return (await response.json()) as ConversationDetail;
    },

    async listConversations(cursor?: string | null): Promise<ConversationPage> {
      const query = new URLSearchParams({ limit: "30" });
      if (cursor) query.set("cursor", cursor);
      const response = await request(`/api/conversations?${query}`);
      if (!response.ok) await fail(response);
      return (await response.json()) as ConversationPage;
    },

    async getWorkspace(): Promise<WorkspaceConfig> {
      const response = await request("/api/workspace/config");
      if (!response.ok) await fail(response);
      return (await response.json()) as WorkspaceConfig;
    },

    async renameConversation(conversationId: string, title: string): Promise<void> {
      const response = await request(`/api/conversations/${conversationId}/title`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ title })
      });
      if (!response.ok) await fail(response);
    },

    async deleteConversation(conversationId: string): Promise<void> {
      const response = await request(`/api/v2/conversations/${conversationId}`, { method: "DELETE" });
      if (!response.ok) await fail(response);
    },

    async exportConversation(conversationId: string, format: "markdown" | "json") {
      const response = await request(`/api/export/${conversationId}/${format}`);
      if (!response.ok) await fail(response);
      const disposition = response.headers.get("content-disposition") ?? "";
      const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
      const plain = /filename="?([^";]+)"?/i.exec(disposition)?.[1];
      const fileName = encoded ? decodeURIComponent(encoded) : (plain ?? `conversation.${format === "json" ? "json" : "md"}`);
      return { blob: await response.blob(), fileName };
    },

    async getAttachment(conversationId: string, messageId: string, index: number): Promise<Blob> {
      const response = await request(`/api/v2/conversations/${conversationId}/messages/${messageId}/attachments/${index}`);
      if (!response.ok) await fail(response);
      return response.blob();
    },

    async getDefaults(): Promise<StoredSettings | null> {
      const response = await request("/api/v2/me/settings");
      if (!response.ok) await fail(response);
      return ((await response.json()) as { defaults: StoredSettings | null }).defaults;
    },

    async putDefaults(settings: StoredSettings): Promise<StoredSettings | null> {
      const response = await request("/api/v2/me/settings", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(settings)
      });
      if (!response.ok) await fail(response);
      return ((await response.json()) as { defaults: StoredSettings | null }).defaults;
    },

    async putConversationSettings(conversationId: string, settings: StoredSettings): Promise<void> {
      const response = await request(`/api/v2/conversations/${conversationId}/settings`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(settings)
      });
      if (!response.ok) await fail(response);
    },

    async listRoles(): Promise<{ roles: UserRole[]; maxRoles: number; defaultRoleId: string | null }> {
      const response = await request("/api/v2/me/roles");
      if (!response.ok) await fail(response);
      const body = (await response.json()) as { roles: UserRole[]; maxRoles: number; defaultRoleId?: string | null };
      return { ...body, defaultRoleId: body.defaultRoleId ?? null };
    },

    async setDefaultRole(roleId: string | null): Promise<void> {
      const response = await request("/api/v2/me/default-role", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ roleId })
      });
      if (!response.ok) await fail(response);
    },

    async createRole(role: { name: string; prompt: string }): Promise<UserRole> {
      const response = await request("/api/v2/me/roles", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(role)
      });
      if (!response.ok) await fail(response);
      return ((await response.json()) as { role: UserRole }).role;
    },

    async updateRole(id: string, role: { name: string; prompt: string }): Promise<UserRole> {
      const response = await request(`/api/v2/me/roles/${id}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(role)
      });
      if (!response.ok) await fail(response);
      return ((await response.json()) as { role: UserRole }).role;
    },

    async deleteRole(id: string): Promise<void> {
      const response = await request(`/api/v2/me/roles/${id}`, { method: "DELETE" });
      if (!response.ok) await fail(response);
    },

    async getAuthStatus(): Promise<AuthStatus> {
      const response = await request("/api/auth/status");
      if (!response.ok) await fail(response);
      const body = (await response.json()) as { user?: { email: string }; safetyThresholds?: string[] };
      return { email: body.user?.email ?? "", safetyThresholds: body.safetyThresholds ?? [] };
    }
  };
}
