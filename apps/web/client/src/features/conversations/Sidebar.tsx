import { useEffect, useMemo, useState } from "react";
import type { ConversationSummary, RunApi } from "../../api/types";
import { themeLabels, type ThemeChoice } from "../../theme/useTheme";
import { ConversationActions } from "./ConversationActions";
import { formatWhen, groupByDay } from "./groupByDay";
import { RenameField } from "./RenameField";

interface Props {
  api: RunApi;
  activeId: string | null;
  /** Changes whenever the list should be fetched again. */
  version: number;
  email: string | null;
  theme: ThemeChoice;
  onCycleTheme: () => void;
  onOpen: (id: string) => void;
  onNew: () => void;
  onSignOut: () => void;
  /** Phone drawer state; on a wide screen the sidebar is always present. */
  open: boolean;
  onClose: () => void;
  /** Ids of conversations with a run in progress in this browser. */
  busyIds?: ReadonlySet<string>;
  /** Called after a conversation was deleted from the list, so the page can leave it. */
  onDeleted: (id: string) => void;
  /** Ids with an unsent draft on this device. */
  draftIds?: ReadonlySet<string>;
}

export function Sidebar({ api, activeId, version, email, theme, onCycleTheme, onOpen, onNew, onSignOut, open, onClose, busyIds, onDeleted, draftIds }: Props) {
  const [items, setItems] = useState<ConversationSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [query, setQuery] = useState("");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [renameError, setRenameError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .listConversations()
      .then(page => {
        if (cancelled) return;
        setItems(page.items);
        setCursor(page.nextCursor);
        setHasMore(page.hasMore);
        setFailed(false);
        setLoading(false);
      })
      .catch(() => {
        if (cancelled) return;
        setFailed(true);
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [api, version]);

  const loadMore = async () => {
    try {
      const page = await api.listConversations(cursor);
      setItems(current => [...current, ...page.items.filter(item => !current.some(known => known.id === item.id))]);
      setCursor(page.nextCursor);
      setHasMore(page.hasMore);
    } catch {
      setFailed(true);
    }
  };

  const shown = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return needle ? items.filter(item => (item.title ?? "未命名对话").toLowerCase().includes(needle)) : items;
  }, [items, query]);
  const groups = useMemo(() => groupByDay(shown), [shown]);

  return (
    <>
      {open && <div className="drawer-backdrop" aria-hidden="true" onClick={onClose} />}
      <nav className="rail" data-open={open || undefined} aria-label="会话">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">球</span>
          <span className="brand-name">
            <strong>球球布丁</strong>
            <small>工作室</small>
          </span>
        </div>

        <button
          type="button"
          className="new"
          onClick={() => {
            onNew();
            onClose();
          }}
        >
          <span aria-hidden="true">＋</span> 新对话
        </button>

        <label className="search">
          <span className="sr-only">搜索会话</span>
          <input type="search" placeholder="搜索会话" value={query} onChange={event => setQuery(event.target.value)} />
        </label>

        <div className="rail-list">
          {loading && <p className="muted pad">正在读取会话…</p>}
          {failed && <p className="muted pad" role="alert">会话列表读取失败。</p>}
          {!loading && !failed && shown.length === 0 && (
            <p className="muted pad">{query ? "没有匹配的会话。" : "还没有会话。"}</p>
          )}
          {groups.map(group => (
            <section key={group.label} aria-label={group.label}>
              <h3 className="group">{group.label}</h3>
              <ul>
                {group.items.map(item => (
                  <li key={item.id} className="row">
                    {editingId === item.id ? (
                      <RenameField
                        initial={item.title ?? ""}
                        error={renameError}
                        onCancel={() => {
                          setEditingId(null);
                          setRenameError(null);
                        }}
                        onSave={async title => {
                          try {
                            await api.renameConversation(item.id, title);
                            setItems(current => current.map(entry => (entry.id === item.id ? { ...entry, title } : entry)));
                            setEditingId(null);
                            setRenameError(null);
                          } catch {
                            setRenameError("重命名失败，请再试一次。");
                          }
                        }}
                      />
                    ) : (
                      <>
                        <button
                          type="button"
                          className={item.id === activeId ? "item active" : "item"}
                          aria-current={item.id === activeId ? "page" : undefined}
                          onClick={() => {
                            onOpen(item.id);
                            onClose();
                          }}
                        >
                          <span className="title">
                            {busyIds?.has(item.id) && <span className="dot" aria-label="生成中" />}
                            {item.title || "未命名对话"}
                            {draftIds?.has(item.id) && <span className="tag draft">草稿</span>}
                          </span>
                          <span className="meta">
                            {item.modelName} · {formatWhen(item.updatedAt)}
                          </span>
                        </button>
                        <ConversationActions
                          api={api}
                          conversationId={item.id}
                          busy={busyIds?.has(item.id) ?? false}
                          variant="sidebar"
                          label={`会话操作：${item.title || "未命名对话"}`}
                          onRename={() => setEditingId(item.id)}
                          onDeleted={() => {
                            setItems(current => current.filter(entry => entry.id !== item.id));
                            onDeleted(item.id);
                          }}
                        />
                      </>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          ))}
          {hasMore && !query && (
            <button type="button" className="more" onClick={() => void loadMore()}>
              加载更多
            </button>
          )}
        </div>

        <div className="account">
          <span className="avatar" aria-hidden="true">{(email ?? "?").slice(0, 1).toUpperCase()}</span>
          <span className="account-text">
            <span className="email">{email}</span>
            <button type="button" className="link" onClick={onSignOut}>退出</button>
          </span>
          <button type="button" className="theme-btn" onClick={onCycleTheme} aria-label={`主题：${themeLabels[theme]}，点击切换`}>
            {themeLabels[theme]}
          </button>
        </div>
      </nav>
    </>
  );
}
