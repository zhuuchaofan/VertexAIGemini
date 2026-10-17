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

    async getAuthStatus(): Promise<AuthStatus> {
      const response = await request("/api/auth/status");
      if (!response.ok) await fail(response);
      const body = (await response.json()) as { user?: { email: string }; safetyThresholds?: string[] };
      return { email: body.user?.email ?? "", safetyThresholds: body.safetyThresholds ?? [] };
    }
  };
}
