import { useEffect, useRef } from "react";
import type { WorkspaceConfig } from "../../api/types";
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
}

const MAX_HEIGHT = 220;

export function Composer(props: Props) {
  const { value, onChange, onSubmit, onStop, running, stopping, canSend, workspace, settings, onSettings, modelInHeader, statusLine, phase, footer } = props;
  const area = useRef<HTMLTextAreaElement>(null);

  // Grow with the text up to a limit, then scroll.
  useEffect(() => {
    const node = area.current;
    if (!node) return;
    node.style.height = "auto";
    node.style.height = `${Math.min(node.scrollHeight, MAX_HEIGHT)}px`;
  }, [value]);

  const model = findModel(workspace, settings.providerId, settings.modelName);
  const canSubmit = canSend && value.trim().length > 0;

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
      <div className="composer-box">
        <textarea
          ref={area}
          id="message"
          aria-label="消息"
          placeholder="输入消息"
          rows={1}
          value={value}
          onChange={event => onChange(event.target.value)}
          onKeyDown={event => {
            if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
              event.preventDefault();
              if (canSubmit) onSubmit();
            }
          }}
        />
        <div className="composer-controls">
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
      {footer && <p className="composer-foot">{footer}</p>}
    </form>
  );
}
