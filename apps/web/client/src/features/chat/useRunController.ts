import { useCallback, useEffect, useReducer, useRef } from "react";
import { ApiError, NetworkError, type RunApi, type RunOptions, type RunSnapshot } from "../../api/types";
import { canSend, initialRunState, runReducer, type RunState } from "./runMachine";

/** A send that was started but whose outcome the browser may not know yet. Survives a reload. */
export interface PendingRun {
  runId: string;
  conversationId: string;
  message: string;
}

export interface PendingStore {
  load(conversationId: string): PendingRun | null;
  save(pending: PendingRun): void;
  clear(conversationId: string): void;
}

export const localPendingStore: PendingStore = {
  load(conversationId) {
    try {
      const raw = localStorage.getItem(`run:pending:${conversationId}`);
      return raw ? (JSON.parse(raw) as PendingRun) : null;
    } catch {
      return null;
    }
  },
  save(pending) {
    try {
      localStorage.setItem(`run:pending:${pending.conversationId}`, JSON.stringify(pending));
    } catch {
      // storage unavailable: the run still works, it just cannot be recovered after a reload
    }
  },
  clear(conversationId) {
    try {
      localStorage.removeItem(`run:pending:${conversationId}`);
    } catch {
      // ignore
    }
  }
};

export interface RunControllerOptions {
  api: RunApi;
  conversationId: string;
  pending?: PendingStore;
  newId?: () => string;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  /** Called once a run reaches a terminal state, so the caller can reload the stored conversation. */
  onTerminal?: (run: RunSnapshot) => void;
}

const RECONCILE_DELAYS_MS = [1000, 1000, 2000, 2000, 4000, 4000, 4000, 8000, 8000, 8000, 8000, 8000];
const STOP_WAIT_MS = 3000;
const SEND_RETRIES = 2;
const defaultSleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));
const defaultNewId = () => crypto.randomUUID();
const defaultNow = () => Date.now();

export interface RunController {
  state: RunState;
  send(text: string, options?: RunOptions): Promise<void>;
  stop(): Promise<void>;
  reset(): void;
  /** The composer took back the unsent text; keep the explanation, drop the pending copy. */
  unsentRestored(): void;
}

/**
 * Drives one conversation's runs against the API: send, stream, stop, and settle after a lost connection.
 * A send is acknowledged by the server's first event; until then the text can safely be sent again with the
 * same run id, and afterwards the only way to learn the outcome is to ask the server.
 */
export function useRunController(options: RunControllerOptions): RunController {
  const { api, conversationId } = options;
  const pending = options.pending ?? localPendingStore;
  const sleep = options.sleep ?? defaultSleep;
  const newId = options.newId ?? defaultNewId;
  const now = options.now ?? defaultNow;

  const [state, dispatch] = useReducer(runReducer, conversationId, initialRunState);
  const stateRef = useRef(state);
  const abortRef = useRef<AbortController | null>(null);
  const terminalRef = useRef(options.onTerminal);
  const streamDone = useRef<Promise<void> | null>(null);
  // Set synchronously, so a double click cannot start two sends before React has re-rendered.
  const sending = useRef(false);

  useEffect(() => {
    stateRef.current = state;
    terminalRef.current = options.onTerminal;
  });

  useEffect(() => {
    dispatch({ type: "reset", conversationId });
  }, [conversationId]);

  const settle = useCallback(
    (run: RunSnapshot) => {
      pending.clear(run.conversationId);
      terminalRef.current?.(run);
    },
    [pending]
  );

  // A run whose stream ended without a terminal event is settled by asking the server, never by guessing.
  useEffect(() => {
    if (state.phase !== "reconciling" || !state.runId) return;
    const runId = state.runId;
    let cancelled = false;

    (async () => {
      for (let attempt = 0; attempt <= RECONCILE_DELAYS_MS.length; attempt += 1) {
        try {
          const run = await api.getRun(runId);
          if (cancelled) return;
          dispatch({ type: "reconciled", run });
          if (run === null) {
            pending.clear(conversationId);
            return;
          }
          if (run.status !== "running") {
            settle(run);
            return;
          }
        } catch {
          // keep trying; the server may simply be unreachable for a moment
        }

        const delay = RECONCILE_DELAYS_MS[attempt];
        if (delay === undefined) break;
        await sleep(delay);
        if (cancelled) return;
      }

      if (!cancelled) dispatch({ type: "reconcileGaveUp" });
    })();

    return () => {
      cancelled = true;
    };
  }, [state.phase, state.runId, api, conversationId, pending, settle, sleep]);

  // After a reload, pick up a send the browser had started but never saw the end of.
  useEffect(() => {
    const saved = pending.load(conversationId);
    if (!saved || stateRef.current.phase !== "idle") return;
    dispatch({ type: "send", runId: saved.runId, message: saved.message, now: now() });
    dispatch({ type: "networkFailed", started: true });
  }, [conversationId, pending, now]);

  const send = useCallback(
    async (text: string, runOptions?: RunOptions) => {
      const message = text.trim();
      if (!message || sending.current || !canSend(stateRef.current)) return;
      sending.current = true;

      const runId = newId();
      dispatch({ type: "send", runId, message, now: now() });
      pending.save({ runId, conversationId, message });

      const abort = new AbortController();
      abortRef.current = abort;

      const run = (async () => {
        for (let attempt = 0; ; attempt += 1) {
          try {
            const result = await api.createRun(
              { ...runOptions, runId, conversationId, message },
              {
                onRun: snapshot => dispatch({ type: "run", run: snapshot }),
                onDelta: delta => dispatch({ type: "delta", delta }),
                onCitations: items => dispatch({ type: "citations", items }),
                onDone: snapshot => {
                  dispatch({ type: "done", run: snapshot });
                  settle(snapshot);
                }
              },
              abort.signal
            );
            if (!result.done) dispatch({ type: "streamEnded" });
            return;
          } catch (error) {
            if (error instanceof ApiError && error.code === "retry_with_same_run_id" && attempt < SEND_RETRIES && !abort.signal.aborted) {
              // The server could not tell whether it accepted the run; asking again with the same id is exactly what it wants.
              await sleep(500 * (attempt + 1));
              continue;
            }

            if (error instanceof ApiError) {
              pending.clear(conversationId);
              dispatch({ type: "apiFailed", status: error.status, error });
              return;
            }

            const started = error instanceof NetworkError && error.started;
            // Before the server acknowledged anything, sending the same run id again is safe.
            if (!started && attempt < SEND_RETRIES && !abort.signal.aborted) {
              await sleep(300 * (attempt + 1));
              continue;
            }

            if (!started) pending.clear(conversationId);
            dispatch({ type: "networkFailed", started });
            return;
          }
        }
      })();

      streamDone.current = run;
      try {
        await run;
      } finally {
        sending.current = false;
      }
    },
    [api, conversationId, newId, now, pending, settle, sleep]
  );

  const stop = useCallback(async () => {
    const current = stateRef.current;
    if (current.phase !== "streaming" || !current.runId) return;
    dispatch({ type: "stopClicked" });

    // Ask the server to stop first (it cannot tell a stop from a dropped connection otherwise), give it a moment to
    // deliver the terminal event, and only then close the stream ourselves.
    const deadline = Date.now() + STOP_WAIT_MS;
    for (;;) {
      try {
        const snapshot = await api.stopRun(current.runId);
        if (snapshot !== null || Date.now() >= deadline) break;
      } catch {
        if (Date.now() >= deadline) break;
      }
      await sleep(150); // the create request may still be in flight
    }

    const finished = streamDone.current;
    let settled = false;
    if (finished) {
      void finished.then(() => {
        settled = true;
      });
      await Promise.race([finished, sleep(STOP_WAIT_MS)]);
    }

    // If the server did not deliver the end of the stream in time, close it ourselves.
    if (!settled) abortRef.current?.abort();
  }, [api, sleep]);

  const reset = useCallback(() => dispatch({ type: "reset", conversationId }), [conversationId]);

  const unsentRestored = useCallback(() => dispatch({ type: "unsentRestored" }), []);

  return { state, send, stop, reset, unsentRestored };
}
