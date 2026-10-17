import { useState } from "react";
import type { WorkspaceConfig } from "../../api/types";
import { Overlay } from "../../ui/Overlay";
import { findModel, type ChatSettings } from "../settings/chatSettings";

interface Props {
  workspace: WorkspaceConfig;
  settings: ChatSettings;
  onChange: (providerId: string, modelName: string) => void;
  /** "chip" sits in the composer, "title" is the tappable model name in the phone header. */
  variant: "chip" | "title";
}

export function ModelPicker({ workspace, settings, onChange, variant }: Props) {
  const [open, setOpen] = useState(false);
  const current = findModel(workspace, settings.providerId, settings.modelName);
  const label = current?.name ?? settings.modelName ?? "选择模型";

  return (
    <Overlay
      open={open}
      onClose={() => setOpen(false)}
      title="选择模型"
      placement={variant === "title" ? "below" : "above"}
      className={`model-picker model-picker--${variant}`}
      trigger={
        <button
          type="button"
          className={variant === "chip" ? "chip" : "title-model"}
          data-overlay-trigger
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen(value => !value)}
        >
          <span className="chip-label">{label}</span>
          <span aria-hidden="true" className="caret">▾</span>
        </button>
      }
    >
      <div className="model-list" role="listbox" aria-label="模型">
        {workspace.providers.map(group => (
          <div key={group.provider.id} role="group" aria-label={group.provider.name}>
            {workspace.providers.length > 1 && <div className="group" aria-hidden="true">{group.provider.name}</div>}
            {group.models.map(model => {
              const selected = group.provider.id === settings.providerId && model.modelName === settings.modelName;
              return (
                <button
                  key={model.modelName}
                  type="button"
                  role="option"
                  aria-selected={selected}
                  className={selected ? "option selected" : "option"}
                  onClick={() => {
                    onChange(group.provider.id, model.modelName);
                    setOpen(false);
                  }}
                >
                  <span className="option-main">
                    <strong>{model.name}</strong>
                    {model.supportsThinking && <span className="tag">思考</span>}
                  </span>
                  {model.description && <span className="option-desc">{model.description}</span>}
                  {selected && <span className="check" aria-hidden="true">✓</span>}
                </button>
              );
            })}
          </div>
        ))}
      </div>
    </Overlay>
  );
}
