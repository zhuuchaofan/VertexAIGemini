import { useState } from "react";
import type { UserRole } from "../../api/types";
import type { Roles } from "./useRoles";

interface Props {
  roles: Roles;
  /** The role whose prompt the conversation is using, if it matches one. */
  current: UserRole | undefined;
  /** The prompt in the conversation's own text box, for "save as role". */
  adHocPrompt: string;
  /** Use this prompt in the conversation (after saving or editing a role). */
  onUse: (prompt: string) => void;
  idPrefix: string;
}

type Mode = { kind: "idle" } | { kind: "new"; prompt: string } | { kind: "edit"; role: UserRole };

/** Create, edit and delete the roles in "我的角色". Roles are copied into a conversation when chosen. */
export function RoleEditor({ roles, current, adHocPrompt, onUse, idPrefix }: Props) {
  const [mode, setMode] = useState<Mode>({ kind: "idle" });
  const [name, setName] = useState("");
  const [prompt, setPrompt] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const open = (next: Mode) => {
    setMode(next);
    setName(next.kind === "edit" ? next.role.name : "");
    setPrompt(next.kind === "edit" ? next.role.prompt : next.kind === "new" ? next.prompt : "");
    setError(null);
    setNote(null);
  };

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "保存失败。");
    } finally {
      setBusy(false);
    }
  };

  const save = () =>
    run(async () => {
      if (mode.kind === "edit") {
        const updated = await roles.update(mode.role.id, { name, prompt });
        // The conversation follows the role it was using; other conversations keep the text they already have.
        if (current?.id === updated.id) onUse(updated.prompt);
      } else {
        const created = await roles.create({ name, prompt });
        onUse(created.prompt);
      }
      setMode({ kind: "idle" });
    });

  const remove = (role: UserRole) => {
    if (!window.confirm(`删除角色「${role.name}」？用过它的会话会保留当时的提示词。`)) return;
    void run(async () => {
      await roles.remove(role.id);
      setNote("已删除。这个会话仍保留这段提示词，显示为「自定义」。");
    });
  };

  if (mode.kind !== "idle") {
    return (
      <div className="role-editor">
        <label htmlFor={`${idPrefix}-role-name`}>角色名</label>
        <input id={`${idPrefix}-role-name`} value={name} maxLength={40} onChange={event => setName(event.target.value)} />
        <label htmlFor={`${idPrefix}-role-prompt`}>提示词</label>
        <textarea id={`${idPrefix}-role-prompt`} rows={8} maxLength={20000} value={prompt} onChange={event => setPrompt(event.target.value)} />
        {error && <p className="field-error" role="alert">{error}</p>}
        <div className="role-actions">
          <button type="button" className="primary" disabled={busy || !name.trim() || !prompt.trim()} onClick={() => void save()}>
            保存角色
          </button>
          <button type="button" className="link" disabled={busy} onClick={() => setMode({ kind: "idle" })}>
            取消
          </button>
        </div>
      </div>
    );
  }

  const full = roles.roles.length >= roles.maxRoles;
  return (
    <div className="role-actions">
      {current && (
        <>
          <button type="button" className="link" onClick={() => open({ kind: "edit", role: current })}>
            编辑角色
          </button>
          <button type="button" className="link" disabled={busy} onClick={() => remove(current)}>
            删除角色
          </button>
        </>
      )}
      {!current && adHocPrompt.trim() && (
        <button type="button" className="link" disabled={full} onClick={() => open({ kind: "new", prompt: adHocPrompt })}>
          存为角色
        </button>
      )}
      <button type="button" className="link" disabled={full} onClick={() => open({ kind: "new", prompt: "" })}>
        新建角色
      </button>
      {full && <span className="hint">最多保存 {roles.maxRoles} 个角色。</span>}
      {error && <p className="field-error" role="alert">{error}</p>}
      {note && <p className="hint" role="status">{note}</p>}
    </div>
  );
}
