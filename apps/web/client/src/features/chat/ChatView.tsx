import { useEffect, useMemo, useRef, useState } from "react";
import type { ConversationDetail, RunApi, RunSnapshot } from "../../api/types";
import { buildThread, runNote } from "./thread";
import { canSend, isActive } from "./runMachine";
import { localPendingStore, useRunController, type PendingStore } from "./useRunController";

interface Props {
  api: RunApi;
  conversationId: string;
  /** Called when a run ends, so the list and the route can follow. */
  onRunEnded?: (run: RunSnapshot) => void;
  /** Called once the server has acknowledged a run, so a new conversation gets an address that survives a reload. */
  onRunAdmitted?: () => void;
  pending?: PendingStore;
}

function useElapsed(startedAt: number | null, running: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(timer);
  }, [running]);
  return startedAt ? Math.max(0, Math.floor((now - startedAt) / 1000)) : 0;
}

const clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;

export function ChatView({ api, conversationId, onRunEnded, onRunAdmitted, pending = localPendingStore }: Props) {
  const [detail, setDetail] = useState<ConversationDetail | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const endRef = useRef<HTMLDivElement>(null);

  const [reloadKey, setReloadKey] = useState(0);

  // Read the stored conversation. State is only set once the answer arrives, never synchronously in the effect.
  useEffect(() => {
    let cancelled = false;
    api
      .getConversation(conversationId)
      .then(loaded => {
        if (cancelled) return;
        setDetail(loaded);
        setLoadError(null);
      })
      .catch(error => {
        if (!cancelled) setLoadError(error instanceof Error ? error.message : "无法读取会话。");
      });
    return () => {
      cancelled = true;
    };
  }, [api, conversationId, reloadKey]);

  const controller = useRunController({ api, conversationId, pending });
  const { state, reset } = controller;

  const admitted = state.phase === "streaming" || state.phase === "stopping";
  useEffect(() => {
    if (admitted) onRunAdmitted?.();
    // the callback only reads the route, it is not a dependency of when to fire
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [admitted]);

  // When a run ends, the stored conversation holds the reply: load it, then let the live view step aside.
  const endedRun = state.phase === "finished" ? (state.terminal ?? null) : null;
  const endedRunId = state.phase === "finished" ? (state.terminal?.runId ?? state.runId) : null;
  useEffect(() => {
    if (!endedRunId) return;
    let cancelled = false;
    api
      .getConversation(conversationId)
      .then(loaded => {
        if (cancelled) return;
        setDetail(loaded);
        reset();
        if (endedRun) onRunEnded?.(endedRun);
      })
      .catch(() => {
        // keep the live view; the reader can press 同步会话
      });
    return () => {
      cancelled = true;
    };
    // endedRun is read once per ended run
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [endedRunId]);

  // A send that never reached the server comes back into the composer without any effect: it is derived.
  const composerText = draft || state.unsentMessage || "";

  const thread = useMemo(() => buildThread(detail), [detail]);
  const elapsed = useElapsed(state.startedAt, isActive(state));

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: "end" });
  }, [thread.length, state.content, state.phase]);

  // After a reload the question is already stored; show the live bubble only for a question that is not.
  const questionStored = detail?.messages.some(m => m.role === "user" && m.runId === state.runId) ?? false;
  const live = state.phase !== "idle" && state.message !== null;

  const submit = async () => {
    const text = composerText.trim();
    if (!text || !canSend(state)) return;
    setDraft("");
    if (state.unsentMessage) controller.unsentRestored();
    await controller.send(text);
  };

  const status = (() => {
    switch (state.phase) {
      case "sending":
        return "正在发送…";
      case "streaming":
        return `正在生成 · ${clock(elapsed)} · 已收到 ${state.content.length} 字`;
      case "stopping":
        return "正在停止…";
      case "reconciling":
        return "连接中断，正在确认结果…";
      default:
        return null;
    }
  })();

  return (
    <section className="chat" aria-label="对话">
      <div className="thread" role="log" aria-live="polite">
        {loadError && <div className="note err">{loadError}</div>}

        {thread.map(item => {
          const note = runNote(item.run);
          return (
            <article key={item.key} className="turn">
              {item.user && <div className="bubble user">{item.user.content}</div>}
              {item.reply && (
                <div className="bubble model" data-status={item.reply.status ?? "final"}>
                  {item.reply.content}
                </div>
              )}
              {note && (
                <div className={`note ${note.tone}`} role="status" data-run-status={item.run?.status}>
                  <strong>{note.title}</strong> {note.detail}
                  {item.run?.status === "interrupted" && (
                    <button type="button" className="link" onClick={() => setReloadKey(key => key + 1)}>
                      同步会话
                    </button>
                  )}
                </div>
              )}
            </article>
          );
        })}

        {live && (
          <article className="turn live">
            {!questionStored && <div className="bubble user">{state.message}</div>}
            {(state.content || state.phase === "streaming") && (
              <div className="bubble model streaming">{state.content}</div>
            )}
          </article>
        )}

        {state.notice && (
          <div className={`note ${state.notice.kind === "error" ? "err" : "warn"}`} role="alert">
            {state.notice.text}
          </div>
        )}
        <div ref={endRef} />
      </div>

      <form
        className="composer"
        onSubmit={event => {
          event.preventDefault();
          void submit();
        }}
      >
        {status && (
          <div className="status" role="status" data-phase={state.phase}>
            <span className="spinner" aria-hidden="true" />
            {status}
          </div>
        )}
        <div className="composer-row">
          <textarea
            id="message"
            aria-label="消息"
            placeholder="输入消息"
            rows={2}
            value={composerText}
            onChange={event => {
              setDraft(event.target.value);
              if (state.unsentMessage) controller.unsentRestored();
            }}
            onKeyDown={event => {
              if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
                event.preventDefault();
                void submit();
              }
            }}
          />
          {state.phase === "streaming" || state.phase === "stopping" ? (
            <button type="button" className="stop" onClick={() => void controller.stop()} disabled={state.phase === "stopping"}>
              停止
            </button>
          ) : (
            <button type="submit" className="send" disabled={!composerText.trim() || !canSend(state)}>
              发送
            </button>
          )}
        </div>
      </form>
    </section>
  );
}
