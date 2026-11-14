import { useEffect, useMemo, useState } from "react";
import type { ConversationSummary, RunApi } from "../../api/types";
import { QuotaBar } from "../quota/QuotaBar";
import type { QuotaStatus } from "../../api/types";
import { ACCENTS, themeLabels, type Accent, type ThemeChoice } from "../../theme/useTheme";
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
  quota?: QuotaStatus | null;
  accent: Accent;
  onAccent: (accent: Accent) => void;
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

export function Sidebar({ api, activeId, version, email, theme, onCycleTheme, quota, accent, onAccent, onOpen, onNew, onSignOut, open, onClose, busyIds, onDeleted, draftIds }: Props) {
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

  // Typing filters what is loaded at once; after a pause the server is asked too, so older conversations are found.
  const [remote, setRemote] = useState<{ query: string; items: ConversationSummary[] } | null>(null);
  const needle = query.trim();
  useEffect(() => {
    if (!needle) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      api
        .searchConversations(needle)
        .then(page => {
          if (!cancelled) setRemote({ query: needle, items: page.items });
        })
        .catch(() => {
          if (!cancelled) setRemote({ query: needle, items: [] });
        });
    }, 300);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [api, needle, version]);
  const searching = Boolean(needle) && remote?.query !== needle;

  const shown = useMemo(() => {
    if (!needle) return items;
    const lower = needle.toLowerCase();
    const local = items.filter(item => (item.title ?? "未命名对话").toLowerCase().includes(lower));
    const older = remote?.query === needle ? remote.items.filter(item => !local.some(known => known.id === item.id)) : [];
    return [...local, ...older].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }, [items, needle, remote]);
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
            <p className="muted pad">{query ? (searching ? "正在搜索…" : "没有匹配的会话。") : "还没有会话。"}</p>
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

        <QuotaBar status={quota ?? null} />

        <div className="accent-row" role="group" aria-label="主题色">
          {ACCENTS.map(item => (
            <button
              key={item.id}
              type="button"
              className="accent-dot"
              style={{ background: item.swatch }}
              aria-label={item.label}
              aria-pressed={accent === item.id}
              title={item.label}
              onClick={() => onAccent(item.id)}
            />
          ))}
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
