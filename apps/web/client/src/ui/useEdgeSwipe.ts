import { useEffect, useRef } from "react";

const EDGE = 28; // px from the left edge where an opening swipe may start
const DISTANCE = 64; // px the finger must travel sideways
const SLOP = 48; // px of vertical drift that still counts as sideways

/**
 * Phone gesture for the conversation drawer: swipe right from the left edge to open it, swipe left to close it.
 * Mostly vertical movement (scrolling) never counts. Inside a horizontally scrolling block (code, tables) it is ignored.
 */
export function useEdgeSwipe(enabled: boolean, open: boolean, setOpen: (open: boolean) => void) {
  const start = useRef<{ x: number; y: number; ignore: boolean } | null>(null);
  const latest = useRef({ open, setOpen });
  useEffect(() => {
    latest.current = { open, setOpen };
  });

  useEffect(() => {
    if (!enabled) return;
    const onStart = (event: TouchEvent) => {
      const touch = event.touches[0];
      if (event.touches.length !== 1 || !touch) {
        start.current = null;
        return;
      }
      const target = event.target as Element | null;
      const scrollsSideways = Boolean(target?.closest?.(".table-wrap, .code pre, textarea, input"));
      start.current = { x: touch.clientX, y: touch.clientY, ignore: scrollsSideways };
    };
    const onEnd = (event: TouchEvent) => {
      const from = start.current;
      start.current = null;
      const touch = event.changedTouches[0];
      if (!from || !touch || from.ignore) return;
      const dx = touch.clientX - from.x;
      const dy = Math.abs(touch.clientY - from.y);
      if (dy > SLOP) return;
      const { open: isOpen, setOpen: set } = latest.current;
      if (!isOpen && from.x <= EDGE && dx >= DISTANCE) set(true);
      else if (isOpen && dx <= -DISTANCE) set(false);
    };
    document.addEventListener("touchstart", onStart, { passive: true });
    document.addEventListener("touchend", onEnd, { passive: true });
    return () => {
      document.removeEventListener("touchstart", onStart);
      document.removeEventListener("touchend", onEnd);
    };
  }, [enabled]);
}
