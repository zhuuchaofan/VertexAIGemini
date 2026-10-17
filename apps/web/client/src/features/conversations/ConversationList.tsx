import { useEffect, useState } from "react";
import type { ConversationSummary, RunApi } from "../../api/types";

interface Props {
  api: RunApi;
  activeId: string | null;
  /** Changes whenever the list should be fetched again. */
  version: number;
  onOpen: (id: string) => void;
  onNew: () => void;
}

export function ConversationList({ api, activeId, version, onOpen, onNew }: Props) {
  const [items, setItems] = useState<ConversationSummary[]>([]);

  useEffect(() => {
    let cancelled = false;
    api
      .listConversations()
      .then(list => {
        if (!cancelled) setItems(list);
      })
      .catch(() => {
        if (!cancelled) setItems([]);
      });
    return () => {
      cancelled = true;
    };
  }, [api, version]);

  return (
    <nav className="rail" aria-label="会话">
      <button type="button" className="new" onClick={onNew}>
        新对话
      </button>
      <ul>
        {items.map(item => (
          <li key={item.id}>
            <button
              type="button"
              className={item.id === activeId ? "item active" : "item"}
              onClick={() => onOpen(item.id)}
            >
              <span className="title">{item.title || "未命名对话"}</span>
              <span className="meta">{item.modelName}</span>
            </button>
          </li>
        ))}
      </ul>
    </nav>
  );
}
