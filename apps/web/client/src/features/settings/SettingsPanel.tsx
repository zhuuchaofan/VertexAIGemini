import { useState } from "react";
import type { WorkspaceConfig } from "../../api/types";
import { Segmented } from "../composer/Segmented";
import { differences, resetSetting, type ChatSettings } from "./chatSettings";
import { SettingsFields } from "./SettingsFields";
import type { Roles } from "./useRoles";

interface Props {
  workspace: WorkspaceConfig;
  safetyThresholds: string[];
  /** This conversation's settings, and how to change them. */
  settings: ChatSettings;
  onSettings: (settings: ChatSettings) => void;
  /** The account's defaults, and how to save them. Resolves when stored; rejects with the reason it was not. */
  defaults: ChatSettings;
  onSaveDefaults: (settings: ChatSettings) => Promise<void>;
  /** Where this conversation's settings live: "new" until the first message creates it. */
  sessionState: "new" | "saved" | "failed";
  /** What a new conversation started from (my defaults plus the default role); "same as default" is measured against it. */
  initial?: ChatSettings;
  /** "我的角色". Without it the panel offers only the built-in presets. */
  roles?: Roles;
  onClose?: () => void;
  idPrefix: string;
}

type Tab = "session" | "defaults";
type SaveState = { kind: "idle" } | { kind: "saving" } | { kind: "saved" } | { kind: "error"; message: string };

const tabs = [
  { value: "session" as const, label: "此会话" },
  { value: "defaults" as const, label: "我的默认" }
];

const sameSettings = (a: ChatSettings, b: ChatSettings) => JSON.stringify({ ...a, customPrompt: "" }) === JSON.stringify({ ...b, customPrompt: "" });

export function SettingsPanel({ workspace, safetyThresholds, settings, onSettings, defaults, onSaveDefaults, sessionState, initial = defaults, roles, onClose, idPrefix }: Props) {
  const [tab, setTab] = useState<Tab>("session");
  const [draft, setDraft] = useState(defaults);
  const [seenDefaults, setSeenDefaults] = useState(defaults);
  const [save, setSave] = useState<SaveState>({ kind: "idle" });

  // A saved default replaces the draft; an edit in progress is kept otherwise.
  if (seenDefaults !== defaults) {
    setSeenDefaults(defaults);
    setDraft(defaults);
  }

  const differing = differences(workspace, settings, initial);

  const saveDefaults = async (value: ChatSettings) => {
    setSave({ kind: "saving" });
    try {
      await onSaveDefaults(value);
      setSave({ kind: "saved" });
    } catch (error) {
      setSave({ kind: "error", message: error instanceof Error ? error.message : "保存失败。" });
    }
  };

  return (
    <div className="settings-panel">
      <div className="panel-top">
        <Segmented label="设置范围" value={tab} options={tabs} onChange={next => { setTab(next); setSave({ kind: "idle" }); }} />
        <p className="hint">
          {tab === "session"
            ? "这个会话的设置会在重新打开时恢复。「我的默认」只用于之后新建的会话。"
            : "新建会话时从这里开始。已有会话不受影响。"}
        </p>
      </div>

      <div className="panel-scroll">
        {tab === "session" ? (
          <>
            <SettingsFields
              workspace={workspace}
              safetyThresholds={safetyThresholds}
              settings={settings}
              onChange={onSettings}
              allowPrompt
              differing={differing}
              roles={roles}
              onReset={key => onSettings(resetSetting(workspace, settings, initial, key))}
              idPrefix={`${idPrefix}-session`}
            />
            {sessionState === "new" && <p className="hint">新对话：发送第一条消息后，这些设置会随会话保存。</p>}
            {sessionState === "failed" && <p className="field-error" role="alert">这些设置没能保存到会话，但仍会用于下一次发送。</p>}
          </>
        ) : (
          <SettingsFields
            workspace={workspace}
            safetyThresholds={safetyThresholds}
            settings={draft}
            onChange={setDraft}
            allowPrompt={false}
            idPrefix={`${idPrefix}-defaults`}
          />
        )}
      </div>

      <footer className="panel-foot">
        {tab === "session" ? (
          <>
            <button type="button" className="ghost" disabled={save.kind === "saving"} onClick={() => void saveDefaults(settings)}>
              将此会话设置存为我的默认
            </button>
            {differing.length > 0 ? (
              <span className="foot-note">
                与默认不同：{differing.length} 项 ·{" "}
                <button type="button" className="link" onClick={() => onSettings(differing.reduce((current, key) => resetSetting(workspace, current, initial, key), settings))}>
                  全部恢复
                </button>
              </span>
            ) : (
              <span className="foot-note">与默认相同</span>
            )}
          </>
        ) : (
          <>
            <button type="button" className="primary" disabled={save.kind === "saving" || sameSettings(draft, defaults)} onClick={() => void saveDefaults(draft)}>
              保存我的默认
            </button>
            {!sameSettings(draft, defaults) && (
              <button type="button" className="link" onClick={() => setDraft(defaults)}>
                放弃修改
              </button>
            )}
          </>
        )}
        <span className="save-state" role="status">
          {save.kind === "saving" && "正在保存…"}
          {save.kind === "saved" && "已保存"}
          {save.kind === "error" && <span className="field-error">{save.message}</span>}
        </span>
        {onClose && (
          <button type="button" className="primary done" onClick={onClose}>
            完成
          </button>
        )}
      </footer>
    </div>
  );
}
