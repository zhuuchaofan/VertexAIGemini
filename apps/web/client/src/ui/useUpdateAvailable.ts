import { useEffect, useState } from "react";
import { entryScript, isNewer, runningEntryScript } from "./updateCheck";

const CHECK_EVERY_MS = 10 * 60 * 1000;
const MIN_GAP_MS = 30 * 1000;

/**
 * Tells when a newer release has been deployed while this page stays open (an installed phone app is kept in the
 * background and never reloads by itself). It looks again when the app comes back to the front and every ten minutes.
 */
export function useUpdateAvailable(): boolean {
  const [available, setAvailable] = useState(false);

  useEffect(() => {
    if (!import.meta.env.PROD || available) return;
    const running = runningEntryScript();
    if (!running) return;
    let last = 0;
    let stopped = false;

    const check = async () => {
      const now = Date.now();
      if (now - last < MIN_GAP_MS) return;
      last = now;
      try {
        const response = await fetch(import.meta.env.BASE_URL, { cache: "no-store", headers: { accept: "text/html" } });
        if (!response.ok) return;
        if (!stopped && isNewer(running, entryScript(await response.text()))) setAvailable(true);
      } catch {
        // offline: try again later
      }
    };

    const onVisible = () => {
      if (document.visibilityState === "visible") void check();
    };
    document.addEventListener("visibilitychange", onVisible);
    window.addEventListener("online", onVisible);
    const timer = setInterval(onVisible, CHECK_EVERY_MS);
    return () => {
      stopped = true;
      document.removeEventListener("visibilitychange", onVisible);
      window.removeEventListener("online", onVisible);
      clearInterval(timer);
    };
  }, [available]);

  return available;
}
