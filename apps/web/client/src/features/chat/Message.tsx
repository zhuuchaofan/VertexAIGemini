import type { Citation, RunSnapshot, StoredMessage } from "../../api/types";
import { CopyButton } from "../../ui/CopyButton";
import { formatClock } from "../conversations/groupByDay";
import { Citations } from "./render/Citations";
import { Markdown } from "./render/Markdown";
import { Thinking } from "./render/Thinking";
import { runNote } from "./thread";

export function UserMessage({ message, children }: { message: Pick<StoredMessage, "content" | "createdAt">; children?: React.ReactNode }) {
  return (
    <div className="msg user">
      <span className="avatar you" aria-hidden="true">你</span>
      <div className="msg-body">
        <div className="msg-head">
          <strong>你</strong>
          {message.createdAt && <time>{formatClock(message.createdAt)}</time>}
        </div>
        {children}
        {message.content && <div className="bubble user">{message.content}</div>}
      </div>
    </div>
  );
}

interface ModelProps {
  modelLabel: string;
  content: string;
  thinking?: string | null;
  citations?: Citation[];
  createdAt?: string;
  status?: "final" | "partial" | null;
  run?: RunSnapshot;
  /** The reply is still arriving. */
  live?: boolean;
  thinkingLive?: boolean;
  /** Offered on an interrupted run so the stored reply can be fetched again. */
  onSync?: () => void;
}

const badge: Record<string, { text: string; tone: "warn" | "err" | "info" }> = {
  interrupted: { text: "已中断", tone: "warn" },
  stopped: { text: "已停止", tone: "info" },
  failed: { text: "失败", tone: "err" }
};

export function ModelMessage({ modelLabel, content, thinking, citations, createdAt, status, run, live = false, thinkingLive = false, onSync }: ModelProps) {
  const note = runNote(run);
  const tag = run ? badge[run.status] : undefined;

  return (
    <div className="msg model">
      <span className="avatar brand-mark" aria-hidden="true">球</span>
      <div className="msg-body">
        <div className="msg-head">
          <strong>{modelLabel}</strong>
          {tag && <span className={`tag ${tag.tone}`}>{tag.text}</span>}
          {createdAt && <time>{formatClock(createdAt)}</time>}
        </div>
        {thinking && <Thinking text={thinking} live={thinkingLive} />}
        {(content || live) && (
          <div className={live ? "bubble model streaming" : "bubble model"} data-status={status ?? "final"}>
            <Markdown text={content} streaming={live} />
          </div>
        )}
        {citations && citations.length > 0 && <Citations items={citations} />}
        {note && (
          <div className={`note ${note.tone}`} role="status" data-run-status={run?.status}>
            <strong>{note.title}</strong> {note.detail}
            {run?.status === "interrupted" && onSync && (
              <button type="button" className="link" onClick={onSync}>
                同步会话
              </button>
            )}
          </div>
        )}
        {!live && content && (
          <div className="msg-actions">
            <CopyButton text={content} />
          </div>
        )}
      </div>
    </div>
  );
}
