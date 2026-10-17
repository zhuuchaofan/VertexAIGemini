import type { Rejection } from "./rules";
import { MAX_ATTACHMENTS, formatSize, labelOf } from "./rules";
import type { PendingAttachment } from "./prepare";

interface Props {
  attachments: PendingAttachment[];
  rejections: Rejection[];
  preparing: boolean;
  onRemove: (id: string) => void;
  onDismissRejections: () => void;
}

/** What is about to be sent: removable chips, and a plain account of anything that was not added, and why. */
export function PendingStrip({ attachments, rejections, preparing, onRemove, onDismissRejections }: Props) {
  if (attachments.length === 0 && rejections.length === 0 && !preparing) return null;

  return (
    <div className="pending">
      {(attachments.length > 0 || preparing) && (
        <ul className="pending-list" aria-label="待发送的附件">
          {attachments.map(item => (
            <li key={item.id} className="pending-chip">
              {item.previewUrl ? (
                <img src={item.previewUrl} alt="" className="pending-thumb" />
              ) : (
                <span className="att-icon" aria-hidden="true">{labelOf(item.fileName)}</span>
              )}
              <span className="att-name">{item.fileName}</span>
              <span className="att-size">{formatSize(item.size)}</span>
              <button type="button" className="pending-remove" aria-label={`移除 ${item.fileName}`} onClick={() => onRemove(item.id)}>
                ×
              </button>
            </li>
          ))}
          {preparing && <li className="pending-chip busy" role="status">正在处理…</li>}
          {attachments.length > 0 && <li className="pending-count" aria-label={`已添加 ${attachments.length} 个，最多 ${MAX_ATTACHMENTS} 个`}>{attachments.length}/{MAX_ATTACHMENTS}</li>}
        </ul>
      )}
      {rejections.length > 0 && (
        <div className="rejections" role="alert">
          <ul>
            {rejections.map((item, index) => (
              <li key={`${item.name}-${index}`}>
                <strong>{item.name} 未添加</strong> {item.reason}
              </li>
            ))}
          </ul>
          <button type="button" className="icon-btn" aria-label="知道了" onClick={onDismissRejections}>
            ×
          </button>
        </div>
      )}
    </div>
  );
}
