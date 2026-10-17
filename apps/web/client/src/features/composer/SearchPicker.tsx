import { useState } from "react";
import type { SearchMode } from "../../api/types";
import { Overlay } from "../../ui/Overlay";
import { Segmented } from "./Segmented";
import { searchLabels } from "../settings/chatSettings";

interface Props {
  value: SearchMode;
  onChange: (value: SearchMode) => void;
}

const options = (Object.keys(searchLabels) as SearchMode[]).map(value => ({ value, label: searchLabels[value] }));

export function SearchPicker({ value, onChange }: Props) {
  const [open, setOpen] = useState(false);
  return (
    <Overlay
      open={open}
      onClose={() => setOpen(false)}
      title="联网"
      className="search-picker"
      trigger={
        <button type="button" className="chip" data-overlay-trigger aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(v => !v)}>
          <span className="chip-key">联网</span>
          <strong className="chip-label">{searchLabels[value]}</strong>
          <span aria-hidden="true" className="caret">▾</span>
        </button>
      }
    >
      <div className="panel-body">
        <Segmented label="联网方式" value={value} options={options} onChange={onChange} />
        <p className="hint">
          {value === "off" && "只用模型自己的知识回答，不联网。"}
          {value === "auto" && "由模型判断是否需要联网查资料。"}
          {value === "force" && "每次都先联网查证，再作答并给出来源。"}
        </p>
      </div>
    </Overlay>
  );
}
