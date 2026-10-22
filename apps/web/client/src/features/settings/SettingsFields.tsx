import type { ModelOption, UserRole, WorkspaceConfig } from "../../api/types";
import { RoleEditor } from "./RoleEditor";
import type { Roles } from "./useRoles";
import { Segmented } from "../composer/Segmented";
import { NumberField } from "./NumberField";
import {
  capabilities,
  findModel,
  findProvider,
  searchLabels,
  withModel,
  type ChatSettings,
  type SettingKey
} from "./chatSettings";
import type { SearchMode } from "../../api/types";

export const safetyLabels: Record<string, string> = {
  OFF: "关闭可配置过滤",
  BLOCK_NONE: "不拦截，保留评分",
  BLOCK_ONLY_HIGH: "仅拦截高风险",
  BLOCK_MEDIUM_AND_ABOVE: "拦截中、高风险",
  BLOCK_LOW_AND_ABOVE: "严格：拦截低、中、高风险"
};

interface Props {
  workspace: WorkspaceConfig;
  safetyThresholds: string[];
  settings: ChatSettings;
  onChange: (settings: ChatSettings) => void;
  /** A custom prompt belongs to a conversation, so "my defaults" has no place for it. */
  allowPrompt: boolean;
  /** Settings that differ from the defaults; each gets a note and a way back. Omitted where there is nothing to compare. */
  differing?: SettingKey[];
  onReset?: (key: SettingKey) => void;
  /** The person's own roles; shown beside the built-in presets where a prompt is allowed. */
  roles?: Roles;
  idPrefix: string;
}

const searchOptions = (Object.keys(searchLabels) as SearchMode[]).map(value => ({ value, label: searchLabels[value] }));

function Heading({ text, field, differing, onReset }: { text: string; field: SettingKey; differing?: SettingKey[]; onReset?: (key: SettingKey) => void }) {
  const changed = differing?.includes(field);
  return (
    <div className="field-head">
      <span>{text}</span>
      {changed && onReset && (
        <span className="changed">
          与默认不同 ·{" "}
          <button type="button" className="link" onClick={() => onReset(field)}>
            重置
          </button>
        </span>
      )}
    </div>
  );
}

/** Every control here exists only if the chosen model supports it; the rest are not drawn at all. */
export function SettingsFields({ workspace, safetyThresholds, settings, onChange, allowPrompt, differing, onReset, roles, idPrefix }: Props) {
  const model: ModelOption | undefined = findModel(workspace, settings.providerId, settings.modelName);
  const provider = findProvider(workspace, settings.providerId);
  const caps = capabilities(model);
  const thinking = model?.thinking;
  const userRoles: UserRole[] = allowPrompt ? (roles?.roles ?? []) : [];
  // A role is copied into the conversation as its custom prompt, so the role in use is the one whose text matches.
  const currentRole = settings.presetId === "custom" ? userRoles.find(role => role.prompt.trim() === settings.customPrompt.trim()) : undefined;
  const presetValue = currentRole ? `role:${currentRole.id}` : settings.presetId;
  const heading = (text: string, field: SettingKey) => <Heading text={text} field={field} differing={differing} onReset={onReset} />;

  return (
    <div className="settings-fields">
      <div className="field-group">
        {heading("模型", "model")}
        <select
          id={`${idPrefix}-model`}
          aria-label="模型"
          value={`${settings.providerId}|${settings.modelName}`}
          onChange={event => {
            const [providerId, modelName] = event.target.value.split("|");
            if (providerId && modelName) onChange(withModel(workspace, settings, providerId, modelName));
          }}
        >
          {workspace.providers.map(group => (
            <optgroup key={group.provider.id} label={group.provider.name}>
              {group.models.map(option => (
                <option key={option.modelName} value={`${group.provider.id}|${option.modelName}`}>
                  {option.name}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
        {model?.description && <p className="hint">{model.description}</p>}
      </div>

      {caps.thinkingKind !== "none" && (
        <div className="field-group">
          {heading("思考", "thinking")}
          {caps.thinkingKind === "level" && (
            <Segmented
              label="思考级别"
              value={settings.thinking.level}
              options={caps.thinkingChoices}
              onChange={level => onChange({ ...settings, thinking: { level } })}
            />
          )}
          {caps.thinkingKind === "toggle-budget" && thinking && (
            <>
              <label className="switch-row">
                <input
                  type="checkbox"
                  checked={settings.thinking.enabled !== false}
                  onChange={event => onChange({ ...settings, thinking: { ...settings.thinking, enabled: event.target.checked } })}
                />
                <span>开启思考</span>
              </label>
              <NumberField
                label="思考预算（token）"
                value={settings.thinking.budget}
                min={1}
                max={1_000_000}
                step={1}
                integer
                placeholder={String(thinking.defaultBudget ?? thinking.budgets[0] ?? "")}
                onChange={budget => onChange({ ...settings, thinking: { ...settings.thinking, budget } })}
              />
            </>
          )}
          {caps.thinkingKind === "fixed" && <p className="hint">这个模型始终开启思考，没有可调整的选项。</p>}
        </div>
      )}

      <div className="field-group">
        {heading("联网", "searchMode")}
        <Segmented label="联网方式" value={settings.searchMode} options={searchOptions} onChange={searchMode => onChange({ ...settings, searchMode })} />
      </div>

      <div className="field-group">
        {heading("提示词预设", "preset")}
        <select
          id={`${idPrefix}-preset`}
          aria-label="提示词预设"
          value={presetValue}
          onChange={event => {
            const value = event.target.value;
            const role = value.startsWith("role:") ? userRoles.find(item => `role:${item.id}` === value) : undefined;
            if (role) onChange({ ...settings, presetId: "custom", customPrompt: role.prompt });
            // Leaving a role for "自定义" starts a blank prompt: keeping the role's text would match the role again and
            // the list would snap straight back to it.
            else if (value === "custom" && currentRole) onChange({ ...settings, presetId: "custom", customPrompt: "" });
            else onChange({ ...settings, presetId: value });
          }}
        >
          {(provider?.presets ?? [])
            .filter(preset => allowPrompt || preset.id !== "custom")
            .map(preset => (
              <option key={preset.id} value={preset.id}>
                {preset.name}
              </option>
            ))}
          {allowPrompt && userRoles.length > 0 && (
            <optgroup label="我的角色">
              {userRoles.map(role => (
                <option key={role.id} value={`role:${role.id}`}>
                  {role.name}
                  {roles?.defaultRoleId === role.id ? "（默认）" : ""}
                </option>
              ))}
            </optgroup>
          )}
        </select>
        {allowPrompt && settings.presetId === "custom" && !currentRole && (
          <textarea
            aria-label="自定义提示词"
            rows={4}
            maxLength={20000}
            placeholder="输入这个会话的系统提示词"
            value={settings.customPrompt}
            onChange={event => onChange({ ...settings, customPrompt: event.target.value })}
          />
        )}
        {allowPrompt && roles && (
          <RoleEditor
            roles={roles}
            current={currentRole}
            adHocPrompt={settings.presetId === "custom" ? settings.customPrompt : ""}
            onUse={prompt => onChange({ ...settings, presetId: "custom", customPrompt: prompt })}
            idPrefix={idPrefix}
          />
        )}
      </div>

      <details className="advanced">
        <summary>高级</summary>
        <div className="advanced-body">
          {caps.sampling ? (
            <>
              <div>
                {heading("温度", "temperature")}
                <NumberField
                  label="温度"
                  value={settings.advanced.temperature}
                  min={0}
                  max={2}
                  step={0.1}
                  placeholder="服务器默认"
                  onChange={temperature => onChange({ ...settings, advanced: omit({ ...settings.advanced, temperature }) })}
                />
              </div>
              <div>
                {heading("Top-P", "topP")}
                <NumberField
                  label="Top-P"
                  value={settings.advanced.topP}
                  min={0}
                  max={1}
                  step={0.05}
                  exclusiveMin
                  placeholder="服务器默认"
                  onChange={topP => onChange({ ...settings, advanced: omit({ ...settings.advanced, topP }) })}
                />
              </div>
            </>
          ) : (
            <p className="hint">这个模型忽略温度和 Top-P，请用思考深度调整推理。</p>
          )}
          <div>
            {heading("最大输出 token", "maxOutputTokens")}
            <NumberField
              label="最大输出 token"
              value={settings.advanced.maxOutputTokens}
              min={1}
              max={caps.maxOutputTokens}
              step={1}
              integer
              placeholder="服务器默认"
              onChange={maxOutputTokens => onChange({ ...settings, advanced: omit({ ...settings.advanced, maxOutputTokens }) })}
            />
          </div>
          {safetyThresholds.length > 0 && (
            <div>
              {heading("内容过滤", "safetyThreshold")}
              <select
                aria-label="内容过滤"
                value={settings.advanced.safetyThreshold ?? ""}
                onChange={event => onChange({ ...settings, advanced: omit({ ...settings.advanced, safetyThreshold: event.target.value || undefined }) })}
              >
                <option value="">跟随服务器策略</option>
                {safetyThresholds.map(threshold => (
                  <option key={threshold} value={threshold}>
                    {safetyLabels[threshold] ?? threshold}
                  </option>
                ))}
              </select>
              <p className="hint">可选级别由你的账号权限决定。</p>
            </div>
          )}
        </div>
      </details>
    </div>
  );
}

function omit<T extends object>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) as T;
}
