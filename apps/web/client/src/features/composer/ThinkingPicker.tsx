import { useState } from "react";
import type { ModelOption } from "../../api/types";
import { Overlay } from "../../ui/Overlay";
import { Segmented } from "./Segmented";
import { capabilities, thinkingLabel, type ThinkingChoice } from "../settings/chatSettings";

interface Props {
  model: ModelOption | undefined;
  value: ThinkingChoice;
  onChange: (value: ThinkingChoice) => void;
}

/** The thinking control for the chosen model. Models without thinking show nothing at all. */
export function ThinkingPicker({ model, value, onChange }: Props) {
  const [open, setOpen] = useState(false);
  const caps = capabilities(model);
  const label = thinkingLabel(model, value);
  if (caps.thinkingKind === "none" || label === null) return null;
  const thinking = model?.thinking;

  return (
    <Overlay
      open={open}
      onClose={() => setOpen(false)}
      title="思考"
      className="thinking-picker"
      trigger={
        <button type="button" className="chip" data-overlay-trigger aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(v => !v)}>
          <span className="chip-key">思考</span>
          <strong className="chip-label">{label}</strong>
          <span aria-hidden="true" className="caret">▾</span>
        </button>
      }
    >
      <div className="panel-body">
        {caps.thinkingKind === "level" && (
          <>
            <Segmented
              label="思考级别"
              value={value.level}
              options={caps.thinkingChoices}
              onChange={level => onChange({ level })}
            />
            <p className="hint">
              {caps.canDisableThinking ? "级别越高回复越慢，也更费额度。" : "这个模型不能关闭思考，只能调整深度。"}
            </p>
          </>
        )}
        {caps.thinkingKind === "toggle-budget" && thinking && (
          <>
            <label className="switch-row">
              <input
                type="checkbox"
                checked={value.enabled !== false}
                onChange={event => onChange({ ...value, enabled: event.target.checked })}
              />
              <span>开启思考</span>
            </label>
            <label className="field">
              <span>思考预算（token）</span>
              <select
                disabled={value.enabled === false}
                value={thinking.budgets.includes(value.budget ?? -1) ? String(value.budget) : "custom"}
                onChange={event => {
                  if (event.target.value !== "custom") onChange({ ...value, budget: Number(event.target.value) });
                }}
              >
                {thinking.budgets.map(budget => (
                  <option key={budget} value={budget}>{budget}</option>
                ))}
                <option value="custom">自定义</option>
              </select>
            </label>
            {!thinking.budgets.includes(value.budget ?? -1) && (
              <label className="field">
                <span>自定义预算</span>
                <input
                  type="number"
                  min={1}
                  step={1}
                  disabled={value.enabled === false}
                  value={value.budget ?? ""}
                  onChange={event => onChange({ ...value, budget: event.target.value === "" ? undefined : Math.floor(Number(event.target.value)) })}
                />
              </label>
            )}
          </>
        )}
        {caps.thinkingKind === "fixed" && <p className="hint">这个模型始终开启思考，没有可调整的选项。</p>}
      </div>
    </Overlay>
  );
}
