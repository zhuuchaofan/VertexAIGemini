import { useCallback, useEffect, useRef, type TouchEvent, type MouseEvent } from "react";

const HOLD_MS = 450;
const MOVE_PX = 10;

/**
 * A press-and-hold on a touch screen. Moving the finger (scrolling) or lifting early cancels it. Returns props for the
 * element. A phone that reports a long press as a context menu opens it the same way.
 */
export function useLongPress(onLongPress: () => void) {
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const origin = useRef<{ x: number; y: number } | null>(null);
  const callback = useRef(onLongPress);
  useEffect(() => {
    callback.current = onLongPress;
  });
  useEffect(() => () => clearTimeout(timer.current), []);

  const cancel = useCallback(() => {
    clearTimeout(timer.current);
    origin.current = null;
  }, []);

  return {
    onTouchStart: (event: TouchEvent) => {
      const touch = event.touches[0];
      if (event.touches.length !== 1 || !touch) return cancel();
      origin.current = { x: touch.clientX, y: touch.clientY };
      clearTimeout(timer.current);
      timer.current = setTimeout(() => {
        origin.current = null;
        callback.current();
      }, HOLD_MS);
    },
    onTouchMove: (event: TouchEvent) => {
      const touch = event.touches[0];
      const from = origin.current;
      if (!touch || !from) return;
      if (Math.abs(touch.clientX - from.x) > MOVE_PX || Math.abs(touch.clientY - from.y) > MOVE_PX) cancel();
    },
    onTouchEnd: cancel,
    onTouchCancel: cancel,
    onContextMenu: (event: MouseEvent) => {
      if (window.matchMedia?.("(pointer: coarse)").matches) {
        event.preventDefault();
        cancel();
        callback.current();
      }
    }
  };
}
