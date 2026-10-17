import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ConversationDetail, RunApi, RunSnapshot, WorkspaceConfig } from "../../api/types";
import { useIsMobile } from "../../ui/useMedia";
import { Overlay } from "../../ui/Overlay";
import { AttachmentList, LocalAttachmentList } from "../attachments/AttachmentList";
import { prepareAttachment, releasePreview, type PendingAttachment } from "../attachments/prepare";
import { selectFiles, type Rejection } from "../attachments/rules";
import { Composer } from "../composer/Composer";
import { ModelPicker } from "../composer/ModelPicker";
import { findModel, fromStored, toRunOptions, toStored, withModel, type ChatSettings } from "../settings/chatSettings";
import { SettingsPanel } from "../settings/SettingsPanel";
import { ModelMessage, UserMessage } from "./Message";
import { canSend, isActive } from "./runMachine";
import { buildThread } from "./thread";
import { localPendingStore, useRunController, type PendingStore } from "./useRunController";

interface Props {
  api: RunApi;
  conversationId: string;
  workspace: WorkspaceConfig;
  /** "My defaults": what a new conversation starts with. */
  defaults: ChatSettings;
  onSaveDefaults: (settings: ChatSettings) => Promise<void>;
  /** The filter levels this account may choose from. */
  safetyThresholds: string[];
  /** Called when a run ends, so the list and the route can follow. */
  onRunEnded?: (run: RunSnapshot) => void;
  /** Called once the server has acknowledged a run, so a new conversation gets an address that survives a reload. */
  onRunAdmitted?: () => void;
  /** Opens the conversation list on a phone. */
  onMenu?: () => void;
  pending?: PendingStore;
}

const INSPECTOR_KEY = "ui:inspector";

function readInspectorOpen(): boolean {
  try {
    return localStorage.getItem(INSPECTOR_KEY) === "1";
  } catch {
    return false;
  }
}

function writeInspectorOpen(open: boolean) {
  try {
    localStorage.setItem(INSPECTOR_KEY, open ? "1" : "0");
  } catch {
    // not remembered
  }
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

export function ChatView({ api, conversationId, workspace, defaults, onSaveDefaults, safetyThresholds, onRunEnded, onRunAdmitted, onMenu, pending = localPendingStore }: Props) {
  const mobile = useIsMobile();
  const [detail, setDetail] = useState<ConversationDetail | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [rejections, setRejections] = useState<Rejection[]>([]);
  const [preparing, setPreparing] = useState(0);
  // What was handed to the run being made, kept on this device until its stored copy can be shown (or the send is taken back).
  const [sent, setSent] = useState<PendingAttachment[]>([]);
  const held = useRef<PendingAttachment[]>([]);
  useEffect(() => {
    held.current = [...attachments, ...sent];
  });
  // Leaving the conversation lets go of the local picture previews.
  useEffect(() => () => held.current.forEach(releasePreview), []);
  const [settings, setSettings] = useState<ChatSettings>(defaults);
  const touched = useRef(false);
  const [settingsOpen, setSettingsOpen] = useState(readInspectorOpen);
  const [sessionState, setSessionState] = useState<"new" | "saved" | "failed">("new");
  const pendingSave = useRef<{ timer: ReturnType<typeof setTimeout>; send: () => void } | null>(null);
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
        // An existing conversation opens on its own settings, unless the person already changed something here.
        if (loaded) {
          setSessionState("saved");
          if (!touched.current) {
            setSettings(current =>
              loaded.settings
                ? fromStored(workspace, loaded.settings, current)
                : findModel(workspace, loaded.providerId, loaded.modelName)
                  ? withModel(workspace, current, loaded.providerId, loaded.modelName)
                  : current
            );
          }
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
        setSent(previous => {
          previous.forEach(releasePreview);
          return [];
        });
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

  // A run that another window started: say so, and pick up its result when it ends. Sending meanwhile is answered by the server.
  const followId = state.phase === "idle" ? (detail?.activeRunId ?? null) : null;
  useEffect(() => {
    if (!followId) return;
    let cancelled = false;
    (async () => {
      for (;;) {
        await new Promise(resolve => setTimeout(resolve, 2000));
        if (cancelled) return;
        try {
          const run = await api.getRun(followId);
          if (cancelled) return;
          if (!run || run.status !== "running") {
            setReloadKey(key => key + 1);
            if (run) onRunEnded?.(run);
            return;
          }
        } catch {
          // keep asking; the network may just be down for a moment
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // onRunEnded only refreshes the list
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [followId, api]);

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

  // A send that never reached the server brings its attachments back along with its text.
  const shownAttachments = attachments.length > 0 ? attachments : state.unsentMessage != null ? sent : [];

  const addFiles = async (files: File[]) => {
    const { accepted, rejected } = selectFiles(files, shownAttachments.length);
    const problems = [...rejected];
    setRejections([]);
    if (attachments.length === 0 && shownAttachments.length > 0) setAttachments(shownAttachments); // took them back from a failed send
    setPreparing(count => count + accepted.length);
    for (const { file, mimeType } of accepted) {
      try {
        const prepared = await prepareAttachment(file, mimeType, crypto.randomUUID());
        setAttachments(current => [...current, prepared]);
      } catch (error) {
        problems.push({ name: file.name, reason: error instanceof Error ? error.message : "处理失败。" });
      } finally {
        setPreparing(count => count - 1);
      }
    }

    setRejections(problems);
  };

  const removeAttachment = (id: string) => {
    const base = attachments.length > 0 ? attachments : shownAttachments;
    const gone = base.find(item => item.id === id);
    if (gone) releasePreview(gone);
    setAttachments(base.filter(item => item.id !== id));
  };

  const submit = async () => {
    const text = composerText.trim();
    const files = shownAttachments;
    if ((!text && files.length === 0) || preparing > 0 || !canSend(state)) return;
    setDraft("");
    setAttachments([]);
    setRejections([]);
    setSent(files);
    if (state.unsentMessage) controller.unsentRestored();
    await controller.send(
      text,
      toRunOptions(workspace, settings),
      files.map(({ base64Data, mimeType, fileName }) => ({ base64Data, mimeType, fileName }))
    );
  };

  // A change is kept with the conversation shortly after the person stops adjusting. Leaving the page does not lose it.
  const flushSave = useCallback(() => {
    const waiting = pendingSave.current;
    if (!waiting) return;
    clearTimeout(waiting.timer);
    pendingSave.current = null;
    waiting.send();
  }, []);
  useEffect(() => flushSave, [flushSave]);

  const updateSettings = (next: ChatSettings) => {
    touched.current = true;
    setSettings(next);
    if (detail === null) return; // not created yet: the first send stores them
    if (pendingSave.current) clearTimeout(pendingSave.current.timer);
    const send = () =>
      api
        .putConversationSettings(conversationId, toStored(workspace, next, { includePrompt: true }))
        .then(() => setSessionState("saved"))
        .catch(() => setSessionState("failed"));
    pendingSave.current = { timer: setTimeout(flushSave, 500), send };
  };

  const toggleSettings = () => {
    setSettingsOpen(open => {
      writeInspectorOpen(!open);
      return !open;
    });
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

  const panel = (onClose?: () => void) => (
    <SettingsPanel
      workspace={workspace}
      safetyThresholds={safetyThresholds}
      settings={settings}
      onSettings={updateSettings}
      defaults={defaults}
      onSaveDefaults={onSaveDefaults}
      sessionState={sessionState}
      onClose={onClose}
      idPrefix={mobile ? "sheet" : "inspector"}
    />
  );

  const settingsButton = (
    <button
      type="button"
      className="icon-btn"
      data-overlay-trigger
      aria-label="对话设置"
      aria-expanded={settingsOpen}
      aria-pressed={mobile ? undefined : settingsOpen}
      onClick={toggleSettings}
      title="对话设置"
    >
      ⚙
    </button>
  );

  return (
    <div className="chat-wrap">
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
        <span className="spacer" />
        {mobile ? (
          <Overlay open={settingsOpen} onClose={() => setSettingsOpen(false)} title="对话设置" trigger={settingsButton} className="settings-sheet">
            {panel(() => setSettingsOpen(false))}
          </Overlay>
        ) : (
          settingsButton
        )}
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
              {item.user && (
                <UserMessage message={item.user}>
                  {item.user.attachments && item.user.attachments.length > 0 && (
                    <AttachmentList api={api} conversationId={conversationId} messageId={item.user.id} items={item.user.attachments} />
                  )}
                </UserMessage>
              )}
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
              {!questionStored && (
                <UserMessage message={{ content: state.message ?? "", createdAt: "" }}>
                  {sent.length > 0 && <LocalAttachmentList items={sent} />}
                </UserMessage>
              )}
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
        statusLine={status ?? (followId ? "这个会话正在另一个窗口生成回复，结束后会自动显示" : null)}
        phase={followId ? "following" : state.phase}
        attachments={shownAttachments}
        rejections={rejections}
        preparing={preparing > 0}
        onAddFiles={files => void addFiles(files)}
        onRemoveAttachment={removeAttachment}
        onDismissRejections={() => setRejections([])}
        footer="内容由 AI 生成，请核对重要信息"
      />
    </section>
    {!mobile && settingsOpen && (
      <aside className="inspector" aria-label="对话设置">
        <header className="inspector-head">
          <h2>设置</h2>
          <button type="button" className="icon-btn" aria-label="关闭设置" onClick={toggleSettings}>×</button>
        </header>
        {panel()}
      </aside>
    )}
    </div>
  );
}
