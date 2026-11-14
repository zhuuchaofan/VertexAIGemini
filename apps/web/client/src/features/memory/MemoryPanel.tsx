import { useEffect, useState } from "react";
import type { MemoryState, RunApi } from "../../api/types";
import { Overlay } from "../../ui/Overlay";

type MemoryApi = Pick<RunApi, "getMemory" | "addMemoryItem" | "removeMemoryItem" | "setMemoryEnabled">;

/**
 * What the assistant remembers about this person. Only what they add here (or with "记住这条" on a message) is kept;
 * the assistant never adds to it by itself. It is read again each time the panel opens.
 */
export function MemoryPanel({ api }: { api: MemoryApi }) {
  const [open, setOpen] = useState(false);
  const [state, setState] = useState<MemoryState | null>(null);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    api
      .getMemory()
      .then(next => {
        if (!cancelled) setState(next);
      })
      .catch(() => {
        if (!cancelled) setError("记忆读取失败，请稍后再试。");
      });
    return () => {
      cancelled = true;
    };
  }, [api, open]);

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "操作失败，请稍后再试。");
    } finally {
      setBusy(false);
    }
  };

  const add = () =>
    run(async () => {
      const text = draft.trim();
      if (!text) return;
      const item = await api.addMemoryItem(text);
      setState(current => (current ? { ...current, items: [...current.items, item] } : current));
      setDraft("");
    });

  const full = state ? state.items.length >= state.maxItems : false;

  return (
    <Overlay
      open={open}
      onClose={() => setOpen(false)}
      title="我的记忆"
      placement="above"
      className="memory"
      trigger={
        <button type="button" className="memory-btn" data-overlay-trigger aria-expanded={open} onClick={() => setOpen(value => !value)}>
          <span aria-hidden="true">🧠</span> 我的记忆
        </button>
      }
    >
      <div className="panel-body memory-body">
        <p className="hint">你保存在这里的内容，会在每次对话时告诉助手。助手不会自己往里添加。</p>
        {state && (
          <label className="switch-row">
            <input
              type="checkbox"
              checked={state.enabled}
              disabled={busy}
              onChange={event => {
                const enabled = event.target.checked;
                void run(async () => setState(await api.setMemoryEnabled(enabled)));
              }}
            />
            对话时参考这些记忆
          </label>
        )}

        {!state && !error && <p className="muted">正在读取…</p>}
        {state && state.items.length === 0 && <p className="muted">还没有记忆。比如“我在上海工作”“回答尽量简短”。</p>}
        {state && state.items.length > 0 && (
          <ul className="memory-list" aria-label="记忆">
            {state.items.map(item => (
              <li key={item.id}>
                <span>{item.text}</span>
                <button
                  type="button"
                  className="icon-btn"
                  aria-label={`删除：${item.text}`}
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      await api.removeMemoryItem(item.id);
                      setState(current => (current ? { ...current, items: current.items.filter(entry => entry.id !== item.id) } : current));
                    })
                  }
                >
                  ×
                </button>
              </li>
            ))}
          </ul>
        )}

        {state && (
          <form
            className="memory-add"
            onSubmit={event => {
              event.preventDefault();
              void add();
            }}
          >
            <input
              value={draft}
              maxLength={state.maxChars}
              placeholder={full ? `最多 ${state.maxItems} 条，先删掉一些` : "让助手记住…"}
              disabled={busy || full}
              aria-label="新的记忆"
              onChange={event => setDraft(event.target.value)}
            />
            <button type="submit" className="primary" disabled={busy || full || !draft.trim()}>
              记住
            </button>
          </form>
        )}
        {state && <p className="hint">{state.items.length} / {state.maxItems} 条</p>}
        {error && <p className="field-error" role="alert">{error}</p>}
      </div>
    </Overlay>
  );
}
