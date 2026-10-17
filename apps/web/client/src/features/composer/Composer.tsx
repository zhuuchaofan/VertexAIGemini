import { useEffect, useRef, useState } from "react";
import type { WorkspaceConfig } from "../../api/types";
import type { PendingAttachment } from "../attachments/prepare";
import { PendingStrip } from "../attachments/PendingStrip";
import { MAX_ATTACHMENTS, type Rejection } from "../attachments/rules";
import { findModel, type ChatSettings } from "../settings/chatSettings";
import { withModel } from "../settings/chatSettings";
import { ModelPicker } from "./ModelPicker";
import { SearchPicker } from "./SearchPicker";
import { ThinkingPicker } from "./ThinkingPicker";

interface Props {
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onStop: () => void;
  /** A run is in progress: the send button becomes stop. */
  running: boolean;
  stopping: boolean;
  canSend: boolean;
  workspace: WorkspaceConfig;
  settings: ChatSettings;
  onSettings: (settings: ChatSettings) => void;
  /** On a phone the model is chosen from the header, not from the composer. */
  modelInHeader: boolean;
  statusLine: string | null;
  phase: string;
  footer?: string;
  /** The text and attachments here are kept on this device until sent. */
  draftSaved?: boolean;
  attachments: PendingAttachment[];
  rejections: Rejection[];
  preparing: boolean;
  onAddFiles: (files: File[]) => void;
  onRemoveAttachment: (id: string) => void;
  onDismissRejections: () => void;
}

const ACCEPT = "image/jpeg,image/png,image/webp,image/gif,application/pdf,.pdf,.txt,.md,.markdown,.csv,.json,.yaml,.yml,.py,.cs,.go,.java,.rs,.sql";

const MAX_HEIGHT = 220;

export function Composer(props: Props) {
  const { value, onChange, onSubmit, onStop, running, stopping, canSend, workspace, settings, onSettings, modelInHeader, statusLine, phase, footer, draftSaved } = props;
  const { attachments, rejections, preparing, onAddFiles, onRemoveAttachment, onDismissRejections } = props;
  const area = useRef<HTMLTextAreaElement>(null);
  const picker = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);

  // Grow with the text up to a limit, then scroll.
  useEffect(() => {
    const node = area.current;
    if (!node) return;
    node.style.height = "auto";
    node.style.height = `${Math.min(node.scrollHeight, MAX_HEIGHT)}px`;
  }, [value]);

  const model = findModel(workspace, settings.providerId, settings.modelName);
  const canSubmit = canSend && !preparing && (value.trim().length > 0 || attachments.length > 0);
  const full = attachments.length >= MAX_ATTACHMENTS;

  return (
    <form
      className="composer"
      onSubmit={event => {
        event.preventDefault();
        if (canSubmit) onSubmit();
      }}
    >
      {statusLine && (
        <div className="status" role="status" data-phase={phase}>
          <span className="spinner" aria-hidden="true" />
          {statusLine}
        </div>
      )}
      <div
        className={dragging ? "composer-box dragging" : "composer-box"}
        onDragOver={event => {
          if (event.dataTransfer.types.includes("Files")) {
            event.preventDefault();
            setDragging(true);
          }
        }}
        onDragLeave={event => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDragging(false);
        }}
        onDrop={event => {
          setDragging(false);
          if (event.dataTransfer.files.length > 0) {
            event.preventDefault();
            onAddFiles(Array.from(event.dataTransfer.files));
          }
        }}
      >
        <PendingStrip
          attachments={attachments}
          rejections={rejections}
          preparing={preparing}
          onRemove={onRemoveAttachment}
          onDismissRejections={onDismissRejections}
        />
        <textarea
          ref={area}
          id="message"
          aria-label="消息"
          placeholder="输入消息"
          rows={1}
          value={value}
          onChange={event => onChange(event.target.value)}
          onPaste={event => {
            // Pasting a picture attaches it; pasting text is left alone.
            const files = Array.from(event.clipboardData.files);
            if (files.length > 0) {
              event.preventDefault();
              onAddFiles(files);
            }
          }}
          onKeyDown={event => {
            if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
              event.preventDefault();
              if (canSubmit) onSubmit();
            }
          }}
        />
        <div className="composer-controls">
          <input
            ref={picker}
            type="file"
            className="sr-only"
            multiple
            accept={ACCEPT}
            tabIndex={-1}
            aria-hidden="true"
            data-testid="file-input"
            onChange={event => {
              const files = Array.from(event.target.files ?? []);
              event.target.value = "";
              if (files.length > 0) onAddFiles(files);
            }}
          />
          <button
            type="button"
            className="icon-btn attach"
            aria-label="添加附件"
            title={full ? `最多 ${MAX_ATTACHMENTS} 个附件` : "添加附件（也可以拖入或粘贴）"}
            disabled={full}
            onClick={() => picker.current?.click()}
          >
            <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
              <path d="M21 11.5l-8.6 8.6a5 5 0 01-7.1-7.1l9-9a3.5 3.5 0 015 5l-9 9a2 2 0 01-2.8-2.8l8.3-8.3" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
          {!modelInHeader && (
            <ModelPicker
              workspace={workspace}
              settings={settings}
              variant="chip"
              onChange={(providerId, modelName) => onSettings(withModel(workspace, settings, providerId, modelName))}
            />
          )}
          <ThinkingPicker model={model} value={settings.thinking} onChange={thinking => onSettings({ ...settings, thinking })} />
          <SearchPicker value={settings.searchMode} onChange={searchMode => onSettings({ ...settings, searchMode })} />
          <span className="spacer" />
          <span className="keys" aria-hidden="true">↵ 发送 · ⇧↵ 换行</span>
          {running ? (
            <button type="button" className="send stop" disabled={stopping} onClick={onStop} aria-label="停止">
              {stopping ? "停止中…" : "停止"}
            </button>
          ) : (
            <button type="submit" className="send" disabled={!canSubmit} aria-label="发送">
              <span aria-hidden="true">↑</span>
              <span className="sr-only">发送</span>
            </button>
          )}
        </div>
      </div>
      {dragging && <p className="drop-hint" role="status">松开以添加附件</p>}
      {(footer || draftSaved) && (
        <p className="composer-foot">
          <span>{footer}</span>
          {draftSaved && <span className="draft-state" role="status">✓ 草稿已自动保存在本机</span>}
        </p>
      )}
    </form>
  );
}
