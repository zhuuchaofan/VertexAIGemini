import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, test, vi } from "vitest";
import { ApiError, NetworkError } from "../../api/types";
import { FakeRunApi, MemoryPendingStore, instantSleep, snapshot } from "./testkit";
import { useRunController } from "./useRunController";

function setup(api: FakeRunApi, pending = new MemoryPendingStore(), onTerminal = vi.fn()) {
  const hook = renderHook(() =>
    useRunController({ api, conversationId: "conv-1", pending, newId: () => "run-1", sleep: instantSleep, now: () => 1000, onTerminal })
  );
  return { ...hook, pending, onTerminal };
}

describe("useRunController", () => {
  test("a send streams, finishes with the server's terminal state, and clears its pending record", async () => {
    const api = new FakeRunApi();
    api.script = async (_request, handlers) => {
      handlers.onRun(snapshot());
      handlers.onDelta({ offset: 0, content: "Hel" });
      handlers.onDelta({ offset: 3, content: "lo" });
      handlers.onDone(snapshot({ status: "completed", outputChars: 5, hasAssistantMessage: true }));
      return { started: true, done: true };
    };
    const { result, pending, onTerminal } = setup(api);

    await act(() => result.current.send("hi"));

    expect(result.current.state.phase).toBe("finished");
    expect(result.current.state.content).toBe("Hello");
    expect(onTerminal).toHaveBeenCalledTimes(1);
    expect(pending.load("conv-1")).toBeNull();
    expect(api.createCalls).toEqual([{ runId: "run-1", conversationId: "conv-1", message: "hi" }]);
  });

  test("a 'cannot tell yet' answer from the server is retried with the same run id", async () => {
    const api = new FakeRunApi();
    let calls = 0;
    api.script = async (_request, handlers) => {
      calls += 1;
      if (calls === 1) throw new ApiError(503, "retry_with_same_run_id", "retry", true);
      handlers.onRun(snapshot());
      handlers.onDone(snapshot({ status: "completed", hasAssistantMessage: true, outputChars: 2 }));
      return { started: true, done: true };
    };
    const { result } = setup(api);

    await act(() => result.current.send("hi"));

    expect(api.createCalls.map(call => call.runId)).toEqual(["run-1", "run-1"]);
    expect(result.current.state.phase).toBe("finished");
    expect(result.current.state.notice).toBeNull();
  });

  test("two clicks in the same tick create exactly one run", async () => {
    const api = new FakeRunApi();
    let release!: () => void;
    api.script = (_request, handlers) =>
      new Promise(resolve => {
        handlers.onRun(snapshot());
        release = () => { handlers.onDone(snapshot({ status: "completed" })); resolve({ started: true, done: true }); };
      });
    const { result } = setup(api);

    let first!: Promise<void>;
    let second!: Promise<void>;
    act(() => {
      first = result.current.send("hi");
      second = result.current.send("hi");
    });
    await waitFor(() => expect(api.createCalls.length).toBe(1));
    release();
    await act(async () => { await first; await second; });

    expect(api.createCalls.length).toBe(1);
  });

  test("before the server acknowledged anything the same run id is sent again, then the text comes back", async () => {
    const api = new FakeRunApi();
    api.script = async () => { throw new NetworkError("offline", false); };
    const { result, pending } = setup(api);

    await act(() => result.current.send("hi"));

    expect(api.createCalls.length).toBe(3); // the first try plus two retries
    expect(new Set(api.createCalls.map(call => call.runId))).toEqual(new Set(["run-1"]));
    expect(result.current.state.phase).toBe("idle");
    expect(result.current.state.unsentMessage).toBe("hi");
    expect(pending.load("conv-1")).toBeNull();
  });

  test("a stream cut after the acknowledgement is settled by asking the server, never by resending", async () => {
    const api = new FakeRunApi();
    api.script = async (_request, handlers) => {
      handlers.onRun(snapshot());
      handlers.onDelta({ offset: 0, content: "partial" });
      throw new NetworkError("reset", true);
    };
    api.onGetRun = n => snapshot({ status: n < 3 ? "running" : "interrupted", statusReason: "connection", hasAssistantMessage: true, outputChars: 7 });
    const { result, onTerminal } = setup(api);

    await act(() => result.current.send("hi"));
    await waitFor(() => expect(result.current.state.phase).toBe("finished"));

    expect(api.createCalls.length).toBe(1);
    expect(api.getRunCalls).toBe(3);
    expect(result.current.state.terminal?.status).toBe("interrupted");
    expect(onTerminal).toHaveBeenCalledTimes(1);
  });

  test("a stream that simply ends without a terminal event is reconciled too", async () => {
    const api = new FakeRunApi();
    api.script = async (_request, handlers) => { handlers.onRun(snapshot()); return { started: true, done: false }; };
    api.onGetRun = () => snapshot({ status: "completed" });
    const { result } = setup(api);

    await act(() => result.current.send("hi"));
    await waitFor(() => expect(result.current.state.phase).toBe("finished"));
    expect(api.getRunCalls).toBe(1);
  });

  test("after a reload a pending send is settled from the server", async () => {
    const api = new FakeRunApi();
    api.onGetRun = () => snapshot({ status: "interrupted", statusReason: "connection" });
    const pending = new MemoryPendingStore();
    pending.save({ runId: "run-1", conversationId: "conv-1", message: "hi" });
    const { result, onTerminal } = setup(api, pending);

    await waitFor(() => expect(result.current.state.phase).toBe("finished"));

    expect(result.current.state.terminal?.status).toBe("interrupted");
    expect(onTerminal).toHaveBeenCalledTimes(1);
    expect(pending.load("conv-1")).toBeNull();
    expect(api.createCalls.length).toBe(0);
  });

  test("after a reload a send the server never saw goes back into the composer", async () => {
    const api = new FakeRunApi();
    api.onGetRun = () => null;
    const pending = new MemoryPendingStore();
    pending.save({ runId: "run-1", conversationId: "conv-1", message: "hi" });
    const { result } = setup(api, pending);

    await waitFor(() => expect(result.current.state.phase).toBe("idle"));
    expect(result.current.state.unsentMessage).toBe("hi");
    expect(pending.load("conv-1")).toBeNull();
  });

  test("stop asks the server first and lets the terminal event arrive before closing the stream", async () => {
    const api = new FakeRunApi();
    let aborted = false;
    api.script = (_request, handlers, signal) =>
      new Promise(resolve => {
        signal.addEventListener("abort", () => { aborted = true; resolve({ started: true, done: false }); });
        handlers.onRun(snapshot());
        handlers.onDelta({ offset: 0, content: "partial" });
        api.onStop = () => {
          // the server cancels the model and then delivers the terminal state on the same stream
          setTimeout(() => { handlers.onDone(snapshot({ status: "stopped", statusReason: "user" })); resolve({ started: true, done: true }); }, 0);
          return snapshot();
        };
      });
    const { result } = setup(api);

    let sent!: Promise<void>;
    act(() => { sent = result.current.send("hi"); });
    await waitFor(() => expect(result.current.state.phase).toBe("streaming"));
    await act(async () => { await result.current.stop(); await sent; });

    expect(api.stopCalls).toEqual(["run-1"]);
    expect(aborted).toBe(false);
    expect(result.current.state.phase).toBe("finished");
    expect(result.current.state.terminal?.status).toBe("stopped");
  });

  test("a refusal because the conversation is busy returns the text and says why", async () => {
    const api = new FakeRunApi();
    api.script = async () => { throw new ApiError(409, "run_in_progress", "此会话正在生成中。", false); };
    const { result } = setup(api);

    await act(() => result.current.send("hi"));

    expect(result.current.state.unsentMessage).toBe("hi");
    expect(result.current.state.notice?.kind).toBe("busy");
  });
});
