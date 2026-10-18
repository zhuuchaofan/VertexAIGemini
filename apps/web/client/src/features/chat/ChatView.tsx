import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ConversationDetail, RunApi, RunSnapshot, WorkspaceConfig } from "../../api/types";
import { useIsMobile } from "../../ui/useMedia";
import { Overlay } from "../../ui/Overlay";
import { AttachmentList, LocalAttachmentList } from "../attachments/AttachmentList";
import { prepareAttachment, releasePreview, type PendingAttachment } from "../attachments/prepare";
import { selectFiles, type Rejection } from "../attachments/rules";
import { ConversationActions } from "../conversations/ConversationActions";
import { RenameField } from "../conversations/RenameField";
import { clearDraft, loadDraft, saveDraft } from "../drafts/drafts";
import { Composer } from "../composer/Composer";
import { ModelPicker } from "../composer/ModelPicker";
import { findModel, fromStored, toRunOptions, toStored, withModel, type ChatSettings } from "../settings/chatSettings";
import { RunInspector } from "../inspector/RunInspector";
import type { Observed } from "../inspector/runDetails";
import { useObserved } from "../inspector/useObserved";
import { Segmented } from "../composer/Segmented";
import { SettingsPanel } from "../settings/SettingsPanel";
import { ModelMessage, UserMessage } from "./Message";
import { canSend, isActive } from "./runMachine";
import { buildThread, canRetry, runNote } from "./thread";
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
  /** Where unsent text and attachments are kept: the conversation's id, or "new" before it exists. */
  draftKey: string;
  /** The draft changed, so the list can tag the conversation. */
  onDraftsChanged?: () => void;
  /** The title changed or the conversation was removed, so the list should be read again. */
  onListChanged?: () => void;
  /** The conversation was deleted from here; the page should leave it. */
  onDeleted?: () => void;
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

export function ChatView({ api, conversationId, workspace, defaults, onSaveDefaults, safetyThresholds, onRunEnded, onRunAdmitted, onMenu, draftKey, onDraftsChanged, onListChanged, onDeleted, pending = localPendingStore }: Props) {
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
  const [renaming, setRenaming] = useState(false);
  const [renameError, setRenameError] = useState<string | null>(null);
  const [draftReady, setDraftReady] = useState(false);
  const [draftSaved, setDraftSaved] = useState(false);

  // Bring back what was typed and attached here last time, unless something new has already been typed.
  useEffect(() => {
    let cancelled = false;
    loadDraft(draftKey)
      .then(async saved => {
        if (cancelled || !saved) return;
        setDraft(current => current || saved.text);
        if (saved.files.length > 0) {
          const restored: PendingAttachment[] = saved.files.map(file => ({
            id: crypto.randomUUID(),
            ...file,
            previewUrl: file.mimeType.startsWith("image/")
              ? URL.createObjectURL(new Blob([Uint8Array.from(atob(file.base64Data), c => c.charCodeAt(0))], { type: file.mimeType }))
              : undefined
          }));
          setAttachments(current => (current.length > 0 ? current : restored));
        }
        setDraftSaved(true);
      })
      .catch(() => undefined)
      .finally(() => {
        if (!cancelled) setDraftReady(true);
      });
    return () => {
      cancelled = true;
    };
  }, [draftKey]);

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
  const observed = useObserved(state);
  const [inspectorTab, setInspectorTab] = useState<"settings" | "run">("settings");
  const [inspectedRunId, setInspectedRunId] = useState<string | null>(null);
  const [inspectedObserved, setInspectedObserved] = useState<Observed | undefined>(undefined);

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
  const questionStored = (state.retryOf !== null) || (detail?.messages.some(m => m.role === "user" && m.runId === state.runId) ?? false);
  const live = state.phase !== "idle" && state.message !== null;
  const model = findModel(workspace, settings.providerId, settings.modelName);
  const labelFor = (providerId: string, modelName: string) => findModel(workspace, providerId, modelName)?.name ?? modelName;
  const replyLabel = detail ? labelFor(detail.providerId, detail.modelName) : labelFor(settings.providerId, settings.modelName);
  // Each reply names the model that really wrote it, which can differ from the conversation's current model.
  const replyLabelFor = (run: { requested?: { providerId?: string; modelName?: string } | null; effective?: { modelName?: string; providerId?: string } | null } | undefined) => {
    const modelName = (run?.effective?.modelName as string | undefined) ?? run?.requested?.modelName;
    const providerId = (run?.effective?.providerId as string | undefined) ?? run?.requested?.providerId;
    return modelName ? labelFor(providerId ?? settings.providerId, modelName) : replyLabel;
  };
  const liveLabel = model?.name ?? settings.modelName;

  // A send that never reached the server brings its attachments back along with its text.
  const shownAttachments = attachments.length > 0 ? attachments : state.unsentMessage != null ? sent : [];

  // Keep the draft as it changes (after a short pause), or drop it once there is nothing left to keep.
  useEffect(() => {
    if (!draftReady) return;
    const timer = setTimeout(() => {
      const files = shownAttachments.map(({ fileName, mimeType, size, base64Data }) => ({ fileName, mimeType, size, base64Data }));
      void saveDraft(draftKey, { text: composerText, files }).then(() => {
        setDraftSaved(composerText.length > 0 || files.length > 0);
        onDraftsChanged?.();
      });
    }, 500);
    return () => clearTimeout(timer);
    // shownAttachments is derived from attachments, sent and the unsent notice, all listed here
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [composerText, attachments, sent, state.unsentMessage, draftKey, draftReady]);

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
    // What was typed is on its way: its draft goes now, whatever the conversation is called by the time the send ends.
    void clearDraft(draftKey).then(() => onDraftsChanged?.());
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

  const openRun = (runId: string) => {
    setInspectedRunId(runId);
    setInspectedObserved({ ...observed.current[runId] } as Observed | undefined);
  };

  const inspectRun = (runId: string) => {
    openRun(runId);
    setInspectorTab("run");
    setSettingsOpen(true);
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

  const inspectedRun = detail?.runs.find(run => run.runId === inspectedRunId) ?? null;
  const tabs = (
    <div className="inspector-tabs">
      <Segmented
        label="检视器"
        value={inspectorTab}
        options={[
          { value: "settings" as const, label: "设置" },
          { value: "run" as const, label: "运行详情" }
        ]}
        onChange={setInspectorTab}
      />
    </div>
  );

  const panel = (onClose?: () => void) => (
    <>
      {tabs}
      {inspectorTab === "run" ? (
        <RunInspector run={inspectedRun} observed={inspectedObserved?.sentAt ? inspectedObserved : undefined} onOpenRun={openRun} />
      ) : (
        settingsPanel(onClose)
      )}
    </>
  );

  const settingsPanel = (onClose?: () => void) => (
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
          {renaming && detail ? (
            <RenameField
              className="bar-rename"
              initial={detail.title ?? ""}
              error={renameError}
              onCancel={() => {
                setRenaming(false);
                setRenameError(null);
              }}
              onSave={async next => {
                try {
                  await api.renameConversation(conversationId, next);
                  setDetail(current => (current ? { ...current, title: next } : current));
                  setRenaming(false);
                  setRenameError(null);
                  onListChanged?.();
                } catch {
                  setRenameError("重命名失败，请再试一次。");
                }
              }}
            />
          ) : (
            <h1>{title}</h1>
          )}
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
        {detail && (
          <ConversationActions
            api={api}
            conversationId={conversationId}
            busy={isActive(state) || followId !== null}
            variant="header"
            label="会话操作"
            onRename={() => setRenaming(true)}
            onDeleted={() => {
              void clearDraft(draftKey).then(() => onDraftsChanged?.());
              onListChanged?.();
              onDeleted?.();
            }}
          />
        )}
        {mobile ? (
          <Overlay open={settingsOpen} onClose={() => setSettingsOpen(false)} title={inspectorTab === "run" ? "运行详情" : "对话设置"} trigger={settingsButton} className="settings-sheet">
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

          {thread.map((item, position) => (
            <article key={item.key} className="turn">
              {item.user && (
                <UserMessage message={item.user}>
                  {item.user.attachments && item.user.attachments.length > 0 && (
                    <AttachmentList api={api} conversationId={conversationId} messageId={item.user.id} items={item.user.attachments} />
                  )}
                </UserMessage>
              )}
              {item.attempts
                .filter(attempt => attempt !== item.finalRun && runNote(attempt))
                .map(attempt => {
                  const note = runNote(attempt)!;
                  return (
                    <div key={attempt.runId} className={`note ${note.tone} earlier`} role="status" data-run-status={attempt.status}>
                      <strong>第 {attempt.attempt} 次：{note.title}</strong> {note.detail}
                    </div>
                  );
                })}
              {(item.reply || item.finalRun) && (
                <ModelMessage
                  modelLabel={replyLabelFor(item.finalRun)}
                  content={item.reply?.content ?? ""}
                  thinking={item.reply?.thinkingContent}
                  citations={item.reply?.citations}
                  createdAt={item.reply?.createdAt}
                  status={item.reply?.status}
                  run={item.finalRun}
                  onSync={() => setReloadKey(key => key + 1)}
                  onInspect={item.finalRun ? () => inspectRun(item.finalRun!.runId) : undefined}
                  onRetry={
                    item.finalRun && canRetry(item, position === thread.length - 1, isActive(state) || followId !== null)
                      ? () => void controller.retry(item.finalRun!.runId, toRunOptions(workspace, settings))
                      : undefined
                  }
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
        draftSaved={draftSaved && (composerText.length > 0 || shownAttachments.length > 0)}
      />
    </section>
    {!mobile && settingsOpen && (
      <aside className="inspector" aria-label="检视器">
        <header className="inspector-head">
          <h2>{inspectorTab === "run" ? "运行详情" : "设置"}</h2>
          <button type="button" className="icon-btn" aria-label="关闭设置" onClick={toggleSettings}>×</button>
        </header>
        {panel()}
      </aside>
    )}
    </div>
  );
}
