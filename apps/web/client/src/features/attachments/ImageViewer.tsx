import { useEffect, useRef } from "react";

interface Props {
  url: string;
  name: string;
  onClose: () => void;
}

/** The picture at full size over the page. Escape or a press on the backdrop closes it and returns focus. */
export function ImageViewer({ url, name, onClose }: Props) {
  const close = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    close.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        onClose();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      opener?.focus();
    };
  }, [onClose]);

  return (
    <div className="viewer" role="dialog" aria-modal="true" aria-label={name} onClick={onClose}>
      <button type="button" className="viewer-close" ref={close} aria-label="关闭预览" onClick={onClose}>
        ×
      </button>
      <img src={url} alt={name} onClick={event => event.stopPropagation()} />
    </div>
  );
}
