import { useSyncExternalStore } from "react";

/** A CSS media query as state, without setting state inside an effect. */
export function useMedia(query: string): boolean {
  return useSyncExternalStore(
    notify => {
      const list = window.matchMedia(query);
      list.addEventListener("change", notify);
      return () => list.removeEventListener("change", notify);
    },
    () => window.matchMedia(query).matches,
    () => false
  );
}

export const useIsMobile = () => useMedia("(max-width: 760px)");
