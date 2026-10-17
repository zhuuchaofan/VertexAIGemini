import { useEffect, useId, useRef, type ReactNode } from "react";

interface Props {
  open: boolean;
  onClose: () => void;
  title: string;
  /** Where the panel anchors on a wide screen. On a phone it is always a bottom sheet. */
  placement?: "above" | "below";
  trigger: ReactNode;
  children: ReactNode;
  className?: string;
}

/**
 * A popover on a wide screen and a bottom sheet on a phone (the switch is CSS). Closes on Escape or an outside
 * press and puts focus back on the control that opened it.
 */
export function Overlay({ open, onClose, title, placement = "above", trigger, children, className }: Props) {
  const wrapper = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const heading = useId();
  const wasOpen = useRef(false);

  useEffect(() => {
    if (!open) {
      if (wasOpen.current) {
        wasOpen.current = false;
        wrapper.current?.querySelector<HTMLElement>("[data-overlay-trigger]")?.focus();
      }
      return;
    }
    wasOpen.current = true;
    panel.current?.focus();

    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        onClose();
      }
    };
    const onPress = (event: PointerEvent) => {
      if (wrapper.current && !wrapper.current.contains(event.target as Node)) onClose();
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPress);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onPress);
    };
  }, [open, onClose]);

  return (
    <div className={`overlay ${className ?? ""}`} ref={wrapper}>
      {trigger}
      {open && (
        <>
          <div className="overlay-backdrop" aria-hidden="true" onClick={onClose} />
          <div
            className="overlay-panel"
            data-placement={placement}
            role="dialog"
            aria-modal="false"
            aria-labelledby={heading}
            tabIndex={-1}
            ref={panel}
          >
            <div className="sheet-grip" aria-hidden="true" />
            <header className="overlay-head">
              <h2 id={heading}>{title}</h2>
              <button type="button" className="icon-btn" aria-label="关闭" onClick={onClose}>
                ×
              </button>
            </header>
            {children}
          </div>
        </>
      )}
    </div>
  );
}
