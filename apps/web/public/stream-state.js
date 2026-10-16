// Pure state machine for one streamed chat reply. It decides what the user is told when the stream
// ends, so a cut-off or failed stream can never be shown as a normal, complete answer.
//
// Terminal kinds:
//   completed   - the server said the model finished normally
//   truncated   - the model hit its output limit (content is usable but incomplete)
//   cancelled   - the user pressed stop (partial content kept)
//   failed      - the request or the generation failed (any partial content kept)
//   interrupted - the connection broke / ended without a final event (partial content kept)

export const STREAM_KINDS = Object.freeze({
  completed: "completed",
  truncated: "truncated",
  cancelled: "cancelled",
  failed: "failed",
  interrupted: "interrupted"
});

const NOTICES = {
  truncated: { tone: "warn", text: "回答因长度限制被截断，内容不完整。" },
  cancelled: { tone: "info", text: "已停止生成，保留了已生成的内容。" },
  failed: { tone: "error", text: "生成失败。" },
  interrupted: { tone: "error", text: "连接中断，回答可能不完整。请检查网络后重试。" }
};

/** Returns { tone, text } for a status that needs a visible notice, or null for a normal completion. */
export function describeStatus(kind, errorMessage) {
  const notice = NOTICES[kind];
  if (!notice) return null;
  if (kind === "failed" && errorMessage) return { tone: notice.tone, text: errorMessage };
  return { ...notice };
}

/** Maps a server "final" payload (or a stored message status) to a stream kind. */
export function kindFromServerStatus(status, succeeded) {
  if (status && STREAM_KINDS[status]) return STREAM_KINDS[status];
  return succeeded === false ? STREAM_KINDS.failed : STREAM_KINDS.completed;
}

export function createStreamTracker() {
  let latest = null;
  let finalPayload = null;
  let conversationId = null;
  let stopRequested = false;

  return {
    onUpdate(payload) {
      if (finalPayload) return; // updates after the final event are stale
      latest = payload;
      conversationId = payload?.conversationId ?? conversationId;
    },

    /** Returns true for the first final event only; duplicates are ignored. */
    onFinal(payload) {
      if (finalPayload) return false;
      finalPayload = payload;
      conversationId = payload?.conversationId ?? conversationId;
      return true;
    },

    /** Marks that the user asked to stop. Safe to call repeatedly. */
    requestStop() {
      const first = !stopRequested;
      stopRequested = true;
      return first;
    },

    get stopRequested() {
      return stopRequested;
    },

    get hasFinal() {
      return finalPayload !== null;
    },

    /**
     * Decides the outcome once the stream is over.
     * @param {{error?: any}} end - the error that ended reading, if any (user aborts included)
     */
    resolve({ error = null } = {}) {
      const partial = {
        content: latest?.content ?? "",
        thinkingContent: latest?.thinkingContent ?? null,
        citations: latest?.citations ?? null,
        conversationId
      };

      // The request never produced a stream (HTTP error, validation, quota...): report it as a failure.
      if (error?.preStream) {
        return { ...partial, kind: STREAM_KINDS.failed, errorMessage: error.message };
      }

      if (finalPayload) {
        const kind = kindFromServerStatus(finalPayload.status, finalPayload.succeeded);
        return {
          kind,
          content: finalPayload.content || partial.content,
          thinkingContent: finalPayload.thinkingContent ?? partial.thinkingContent,
          citations: finalPayload.citations ?? partial.citations,
          errorMessage: finalPayload.errorMessage ?? null,
          conversationId
        };
      }

      // No final event: the stream was cut, either by the user or by the network/proxy.
      if (stopRequested) {
        return { ...partial, kind: STREAM_KINDS.cancelled, errorMessage: null };
      }

      return {
        ...partial,
        kind: STREAM_KINDS.interrupted,
        errorMessage: error?.message && error.name !== "AbortError" ? error.message : null
      };
    }
  };
}
