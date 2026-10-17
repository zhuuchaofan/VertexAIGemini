import { useState } from "react";
import type { RunApi } from "../../api/types";
import { Overlay } from "../../ui/Overlay";
import { downloadBlob } from "./download";

interface Props {
  api: RunApi;
  conversationId: string;
  /** A run is generating here: it cannot be deleted until it ends. */
  busy: boolean;
  onRename: () => void;
  onDeleted: () => void;
  /** "sidebar" is the small "…" on a list row, "header" the one beside the title. */
  variant: "sidebar" | "header";
  label: string;
}

type Step = "menu" | "confirm-delete";

/**
 * Rename, export and delete for one conversation. Deleting asks for a second press in the same place, says what will
 * go with it, and reports it plainly if the server refuses.
 */
export function ConversationActions({ api, conversationId, busy, onRename, onDeleted, variant, label }: Props) {
  const [open, setOpen] = useState(false);
  const [step, setStep] = useState<Step>("menu");
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState(false);

  const close = () => {
    setOpen(false);
    setStep("menu");
    setError(null);
  };

  const exportAs = async (format: "markdown" | "json") => {
    setWorking(true);
    setError(null);
    try {
      const file = await api.exportConversation(conversationId, format);
      downloadBlob(file.blob, file.fileName);
      close();
    } catch {
      setError("导出失败，请稍后再试。");
    } finally {
      setWorking(false);
    }
  };

  const remove = async () => {
    setWorking(true);
    setError(null);
    try {
      await api.deleteConversation(conversationId);
      close();
      onDeleted();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "删除失败。");
      setStep("menu");
    } finally {
      setWorking(false);
    }
  };

  return (
    <Overlay
      open={open}
      onClose={close}
      title="会话操作"
      placement="below"
      className={`conv-actions ${variant}`}
      trigger={
        <button
          type="button"
          className="icon-btn dots"
          data-overlay-trigger
          aria-haspopup="dialog"
          aria-expanded={open}
          aria-label={label}
          onClick={event => {
            event.stopPropagation();
            setOpen(value => !value);
          }}
        >
          ⋯
        </button>
      }
    >
      <div className="menu" role="menu" aria-label="会话操作">
        {step === "menu" ? (
          <>
            <button type="button" role="menuitem" className="menu-item" onClick={() => {
                close();
                // After the menu has handed focus back to its button, so the rename field keeps the focus it asks for.
                setTimeout(onRename, 0);
              }}>
              重命名
            </button>
            <button type="button" role="menuitem" className="menu-item" disabled={working} onClick={() => void exportAs("markdown")}>
              导出为 Markdown
            </button>
            <button type="button" role="menuitem" className="menu-item" disabled={working} onClick={() => void exportAs("json")}>
              导出为 JSON
            </button>
            <button
              type="button"
              role="menuitem"
              className="menu-item danger"
              disabled={busy}
              title={busy ? "正在生成，结束后才能删除" : undefined}
              onClick={() => setStep("confirm-delete")}
            >
              删除…
            </button>
            {busy && <p className="hint pad">正在生成回复，结束后才能删除。</p>}
          </>
        ) : (
          <div className="confirm">
            <p><strong>删除这个会话？</strong></p>
            <p className="hint">消息和已上传的附件会一起删除，无法恢复。</p>
            <div className="confirm-actions">
              <button type="button" className="ghost" onClick={() => setStep("menu")}>取消</button>
              <button type="button" className="danger-btn" disabled={working} onClick={() => void remove()}>
                {working ? "正在删除…" : "删除"}
              </button>
            </div>
          </div>
        )}
        {error && <p className="field-error pad" role="alert">{error}</p>}
      </div>
    </Overlay>
  );
}
