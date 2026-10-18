import { useEffect, useRef, type RefObject } from "react";
import type { RunState } from "../chat/runMachine";
import type { Observed } from "./runDetails";

/**
 * What this window itself watched: when a send went out, when its first output arrived and when it ended.
 * Kept in memory only (a ref, read when the inspector is opened); it is a browser-side measurement and is never
 * mixed with the server's times.
 */
export function useObserved(state: RunState): RefObject<Record<string, Observed>> {
  const observed = useRef<Record<string, Observed>>({});
  const hasOutput = state.content.length > 0 || state.thinking.length > 0;
  const endedId = state.phase === "finished" ? (state.terminal?.runId ?? state.runId) : null;

  useEffect(() => {
    const { runId, startedAt } = state;
    if (runId && startedAt !== null && !observed.current[runId]) {
      observed.current[runId] = { sentAt: startedAt, firstOutputAt: null, endedAt: null };
    }
  }, [state]);

  useEffect(() => {
    const runId = state.runId;
    const entry = runId ? observed.current[runId] : undefined;
    if (runId && entry && hasOutput && entry.firstOutputAt === null) observed.current[runId] = { ...entry, firstOutputAt: Date.now() };
  }, [state.runId, hasOutput]);

  useEffect(() => {
    const entry = endedId ? observed.current[endedId] : undefined;
    if (endedId && entry && entry.endedAt === null) observed.current[endedId] = { ...entry, endedAt: Date.now() };
  }, [endedId]);

  return observed;
}
