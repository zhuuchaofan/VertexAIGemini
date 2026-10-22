import type { RunNote } from "./thread";

/** The "what to do" line under a failure, and, for administrators only, the technical details and operator steps. */
export function NoteExtras({ note }: { note: Pick<RunNote, "hint" | "admin"> }) {
  if (!note.hint && !note.admin) return null;
  return (
    <>
      {note.hint && <p className="note-hint">怎么办：{note.hint}</p>}
      {note.admin && (
        <details className="note-admin">
          <summary>管理员信息</summary>
          {note.admin.detail && <p className="mono">{note.admin.detail}</p>}
          {note.admin.hint && <p>解决办法：{note.admin.hint}</p>}
        </details>
      )}
    </>
  );
}
