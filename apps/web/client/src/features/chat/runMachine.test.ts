import { describe, expect, test } from "vitest";
import type { RunSnapshot } from "../../api/types";
import { canSend, initialRunState, runReducer, type RunAction, type RunState } from "./runMachine";

const snapshot = (overrides: Partial<RunSnapshot> = {}): RunSnapshot => ({
  runId: "r1", conversationId: "c1", userMessageId: "u", assistantMessageId: "a", status: "running",
  statusReason: null, attempt: 1, error: null, outputChars: 0, hasAssistantMessage: false,
  providerCallStarted: false, admittedAt: null, endedAt: null, ...overrides
});

const play = (actions: RunAction[], from: RunState = initialRunState("c1")) => actions.reduce(runReducer, from);
const send: RunAction = { type: "send", runId: "r1", message: "hi", now: 1000 };

describe("runReducer", () => {
  test("a normal send streams and finishes with the server's terminal state", () => {
    const state = play([
      send,
      { type: "run", run: snapshot() },
      { type: "delta", delta: { offset: 0, content: "Hel" } },
      { type: "delta", delta: { offset: 3, content: "lo" } },
      { type: "done", run: snapshot({ status: "completed" }) }
    ]);
    expect(state.phase).toBe("finished");
    expect(state.content).toBe("Hello");
    expect(state.terminal?.status).toBe("completed");
  });

  test("sending is refused while a run is active, so a double click starts nothing", () => {
    const active = play([send, { type: "run", run: snapshot() }]);
    expect(canSend(active)).toBe(false);
    expect(runReducer(active, { ...send, runId: "r2" })).toBe(active);
  });

  test("a duplicated delta is ignored and a skipped one is flagged, never merged out of order", () => {
    const base = play([send, { type: "run", run: snapshot() }, { type: "delta", delta: { offset: 0, content: "abc" } }]);
    expect(runReducer(base, { type: "delta", delta: { offset: 0, content: "abc" } }).content).toBe("abc");
    const gap = runReducer(base, { type: "delta", delta: { offset: 9, content: "zzz" } });
    expect(gap.content).toBe("abc");
    expect(gap.gap).toBe(true);
  });

  test("thinking and answer text have separate offsets", () => {
    const state = play([
      send, { type: "run", run: snapshot() },
      { type: "delta", delta: { offset: 0, thinking: "plan" } },
      { type: "delta", delta: { offset: 0, content: "answer" } },
      { type: "delta", delta: { offset: 4, thinking: "!" } }
    ]);
    expect(state.thinking).toBe("plan!");
    expect(state.content).toBe("answer");
    expect(state.gap).toBe(false);
  });

  test("a stream that ends before the server acknowledged anything puts the text back in the composer", () => {
    const state = play([send, { type: "streamEnded" }]);
    expect(state.phase).toBe("idle");
    expect(state.unsentMessage).toBe("hi");
    expect(state.notice?.kind).toBe("unsent");
  });

  test("a stream lost after the acknowledgement is reconciled, not reported as failed", () => {
    const state = play([send, { type: "run", run: snapshot() }, { type: "networkFailed", started: true }]);
    expect(state.phase).toBe("reconciling");
    expect(state.unsentMessage).toBeNull();
  });

  test("stop moves a streaming run to stopping, and the terminal event ends it", () => {
    const stopping = play([send, { type: "run", run: snapshot() }, { type: "stopClicked" }]);
    expect(stopping.phase).toBe("stopping");
    expect(runReducer(stopping, { type: "done", run: snapshot({ status: "stopped" }) }).phase).toBe("finished");
    expect(runReducer(play([send]), { type: "stopClicked" }).phase).toBe("sending"); // nothing to stop yet
  });

  test("reconciliation: still running keeps waiting, terminal finishes, unknown puts the text back", () => {
    const lost = play([send, { type: "run", run: snapshot() }, { type: "streamEnded" }]);
    expect(runReducer(lost, { type: "reconciled", run: snapshot() }).phase).toBe("reconciling");
    expect(runReducer(lost, { type: "reconciled", run: snapshot({ status: "interrupted" }) }).phase).toBe("finished");
    const notFound = runReducer(lost, { type: "reconciled", run: null });
    expect(notFound.phase).toBe("idle");
    expect(notFound.unsentMessage).toBe("hi");
  });

  test("giving up on reconciliation says so instead of guessing", () => {
    const lost = play([send, { type: "run", run: snapshot() }, { type: "streamEnded" }, { type: "reconcileGaveUp" }]);
    expect(lost.phase).toBe("finished");
    expect(lost.notice?.kind).toBe("unconfirmed");
  });

  test.each([
    [409, { code: "run_in_progress", message: "busy", retryable: false }, "busy"],
    [400, { code: "invalid_options", message: "bad", retryable: false }, "error"],
    [503, { code: "retry_with_same_run_id", message: "later", retryable: true }, "unsent"]
  ])("a refusal with status %s before any event returns the text and explains why", (status, error, kind) => {
    const state = play([send, { type: "apiFailed", status, error }]);
    expect(state.phase).toBe("idle");
    expect(state.unsentMessage).toBe("hi");
    expect(state.notice?.kind).toBe(kind);
  });

  test("a terminal event that arrives after the run was already settled changes nothing", () => {
    const finished = play([send, { type: "run", run: snapshot() }, { type: "done", run: snapshot({ status: "completed" }) }]);
    expect(runReducer(finished, { type: "done", run: snapshot({ status: "failed" }) })).toBe(finished);
  });
});
