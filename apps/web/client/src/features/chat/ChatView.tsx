import { useEffect, useMemo, useRef, useState } from "react";
import type { ConversationDetail, RunApi, RunSnapshot, WorkspaceConfig } from "../../api/types";
import { useIsMobile } from "../../ui/useMedia";
import { Composer } from "../composer/Composer";
import { ModelPicker } from "../composer/ModelPicker";
import { defaultSettings, findModel, toRunOptions, withModel, type ChatSettings } from "../settings/chatSettings";
import { ModelMessage, UserMessage } from "./Message";
import { canSend, isActive } from "./runMachine";
import { buildThread } from "./thread";
import { localPendingStore, useRunController, type PendingStore } from "./useRunController";

interface Props {
  api: RunApi;
  conversationId: string;
  workspace: WorkspaceConfig;
  /** Called when a run ends, so the list and the route can follow. */
  onRunEnded?: (run: RunSnapshot) => void;
  /** Called once the server has acknowledged a run, so a new conversation gets an address that survives a reload. */
  onRunAdmitted?: () => void;
  /** Opens the conversation list on a phone. */
  onMenu?: () => void;
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

export function ChatView({ api, conversationId, workspace, onRunEnded, onRunAdmitted, onMenu, pending = localPendingStore }: Props) {
  const mobile = useIsMobile();
  const [detail, setDetail] = useState<ConversationDetail | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [settings, setSettings] = useState<ChatSettings>(() => defaultSettings(workspace));
  const touched = useRef(false);
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
        // An existing conversation opens on the model it was using, unless the person already picked another.
        if (loaded && !touched.current) {
          setSettings(current =>
            findModel(workspace, loaded.providerId, loaded.modelName)
              ? withModel(workspace, current, loaded.providerId, loaded.modelName)
              : current
          );
        }
      })
      .catch(error => {
        if (!cancelled) setLoadError(error instanceof Error ? error.message : "无法读取会话。");
      });
    return () => {
      cancelled = true;
    };
  }, [api, conversationId, reloadKey, workspace]);

  const controller = useRunController({ api, conversationId, pending });
  const { state, reset } = controller;

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

  const admitted = state.phase === "streaming" || state.phase === "stopping";
  useEffect(() => {
    if (admitted) onRunAdmitted?.();
    // the callback only reads the route, it is not a dependency of when to fire
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [admitted]);

  // A send that never reached the server comes back into the composer without any effect: it is derived.
  const composerText = draft || state.unsentMessage || "";

  const thread = useMemo(() => buildThread(detail), [detail]);
  const elapsed = useElapsed(state.startedAt, isActive(state));

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: "end" });
  }, [thread.length, state.content, state.thinking, state.phase]);

  // After a reload the question is already stored; show the live bubble only for a question that is not.
  const questionStored = detail?.messages.some(m => m.role === "user" && m.runId === state.runId) ?? false;
  const live = state.phase !== "idle" && state.message !== null;
  const model = findModel(workspace, settings.providerId, settings.modelName);
  const labelFor = (providerId: string, modelName: string) => findModel(workspace, providerId, modelName)?.name ?? modelName;
  const replyLabel = detail ? labelFor(detail.providerId, detail.modelName) : labelFor(settings.providerId, settings.modelName);
  const liveLabel = model?.name ?? settings.modelName;

  const submit = async () => {
    const text = composerText.trim();
    if (!text || !canSend(state)) return;
    setDraft("");
    if (state.unsentMessage) controller.unsentRestored();
    await controller.send(text, toRunOptions(workspace, settings));
  };

  const updateSettings = (next: ChatSettings) => {
    touched.current = true;
    setSettings(next);
  };

  const running = state.phase === "sending" || state.phase === "streaming" || state.phase === "stopping";
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

  const title = detail?.title || (state.message ? state.message.slice(0, 40) : "新对话");
  const saved = detail !== null && !isActive(state);

  return (
    <section className="chat" aria-label="对话">
      <header className="bar">
        {mobile && (
          <button type="button" className="icon-btn" aria-label="会话列表" onClick={onMenu}>
            ☰
          </button>
        )}
        <div className="bar-title">
          <h1>{title}</h1>
          {mobile ? (
            <ModelPicker
              workspace={workspace}
              settings={settings}
              variant="title"
              onChange={(providerId, modelName) => updateSettings(withModel(workspace, settings, providerId, modelName))}
            />
          ) : (
            saved && <span className="saved">✓ 已保存</span>
          )}
        </div>
      </header>

      <div className="thread-scroll">
        <div className="thread" role="log" aria-live="polite">
          {loadError && <div className="note err">{loadError}</div>}
          {!detail && !live && !loadError && thread.length === 0 && (
            <div className="empty">
              <span className="brand-mark" aria-hidden="true">球</span>
              <h2>有什么可以帮你？</h2>
              <p>直接输入问题。下方可以切换模型、思考深度和联网方式。</p>
            </div>
          )}

          {thread.map(item => (
            <article key={item.key} className="turn">
              {item.user && <UserMessage message={item.user} />}
              {(item.reply || item.run) && (
                <ModelMessage
                  modelLabel={replyLabel}
                  content={item.reply?.content ?? ""}
                  thinking={item.reply?.thinkingContent}
                  citations={item.reply?.citations}
                  createdAt={item.reply?.createdAt}
                  status={item.reply?.status}
                  run={item.run}
                  onSync={() => setReloadKey(key => key + 1)}
                />
              )}
            </article>
          ))}

          {live && (
            <article className="turn live">
              {!questionStored && <UserMessage message={{ content: state.message ?? "", createdAt: "" }} />}
              {(state.content || state.thinking || state.phase === "streaming") && (
                <ModelMessage
                  modelLabel={liveLabel}
                  content={state.content}
                  thinking={state.thinking}
                  citations={state.citations}
                  live
                  thinkingLive={Boolean(state.thinking) && !state.content}
                />
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
      </div>

      <Composer
        value={composerText}
        onChange={text => {
          setDraft(text);
          if (state.unsentMessage) controller.unsentRestored();
        }}
        onSubmit={() => void submit()}
        onStop={() => void controller.stop()}
        running={running}
        stopping={state.phase === "stopping"}
        canSend={canSend(state)}
        workspace={workspace}
        settings={settings}
        onSettings={updateSettings}
        modelInHeader={mobile}
        statusLine={status}
        phase={state.phase}
        footer="内容由 AI 生成，请核对重要信息"
      />
    </section>
  );
}
