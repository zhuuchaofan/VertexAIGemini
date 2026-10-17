import { useState } from "react";
import type { AttachmentView, RunApi } from "../../api/types";
import { ImageViewer } from "./ImageViewer";
import { formatSize, isImageType, labelOf } from "./rules";
import { useAttachmentBlob } from "./useAttachmentBlob";

interface StoredProps {
  api: RunApi;
  conversationId: string;
  messageId: string;
  items: AttachmentView[];
}

function FileChip({ name, size, caption, onClick, disabled }: { name: string; size: number; caption?: string; onClick?: () => void; disabled?: boolean }) {
  return (
    <button type="button" className="att-chip" disabled={disabled} onClick={onClick} data-state={caption ? "unavailable" : undefined}>
      <span className="att-icon" aria-hidden="true">{labelOf(name)}</span>
      <span className="att-name">{name}</span>
      <span className="att-size">{caption ?? formatSize(size)}</span>
    </button>
  );
}

function StoredImage({ api, conversationId, messageId, item }: { api: RunApi; conversationId: string; messageId: string; item: AttachmentView }) {
  const blob = useAttachmentBlob(api, conversationId, messageId, item.index, item.state === "stored");
  const [open, setOpen] = useState(false);

  if (blob.status === "ready") {
    return (
      <>
        <button type="button" className="att-thumb" aria-label={`查看图片 ${item.fileName}`} onClick={() => setOpen(true)}>
          <img src={blob.url} alt={item.fileName} />
          <span className="att-cap">{item.fileName} · {formatSize(item.sizeBytes)}</span>
        </button>
        {open && <ImageViewer url={blob.url} name={item.fileName} onClose={() => setOpen(false)} />}
      </>
    );
  }

  return (
    <div className="att-thumb att-empty" role="img" aria-label={item.fileName}>
      <span>{blob.status === "loading" && item.state === "stored" ? "加载中…" : item.state === "pending" ? "保存中…" : "不可用"}</span>
      <span className="att-cap">{item.fileName}</span>
    </div>
  );
}

/** Files and pictures already part of a message. A picture loads in place; a file downloads when pressed. */
export function AttachmentList({ api, conversationId, messageId, items }: StoredProps) {
  const download = async (item: AttachmentView) => {
    try {
      const blob = await api.getAttachment(conversationId, messageId, item.index);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = item.fileName;
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch {
      // the chip stays; pressing it again tries again
    }
  };

  return (
    <div className="att-list">
      {items.map(item =>
        isImageType(item.mimeType) ? (
          <StoredImage key={item.index} api={api} conversationId={conversationId} messageId={messageId} item={item} />
        ) : (
          <FileChip
            key={item.index}
            name={item.fileName}
            size={item.sizeBytes}
            disabled={item.state !== "stored"}
            caption={item.state === "pending" ? "保存中…" : item.state === "failed" ? "不可用" : undefined}
            onClick={() => void download(item)}
          />
        )
      )}
    </div>
  );
}

interface LocalItem {
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  previewUrl?: string;
}

/** The same strip for a message that was just sent, from what is still on this device. */
export function LocalAttachmentList({ items }: { items: LocalItem[] }) {
  return (
    <div className="att-list">
      {items.map(item =>
        item.previewUrl ? (
          <div key={item.id} className="att-thumb">
            <img src={item.previewUrl} alt={item.fileName} />
            <span className="att-cap">{item.fileName} · {formatSize(item.size)}</span>
          </div>
        ) : (
          <FileChip key={item.id} name={item.fileName} size={item.size} disabled />
        )
      )}
    </div>
  );
}
