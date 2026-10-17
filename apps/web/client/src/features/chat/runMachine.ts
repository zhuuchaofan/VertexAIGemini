import type { Citation, DeltaEvent, RunError, RunSnapshot } from "../../api/types";

/**
 * The client-side life of one send, as a pure reducer.
 *
 *   idle ─send→ sending ─run→ streaming ─done→ finished
 *                  │             │ └stop→ stopping ─done→ finished
 *                  │             └ stream lost → reconciling ─(poll)→ finished
 *                  └ never acknowledged → idle (the text goes back to the composer)
 *
 * It only ever shows what the server confirmed: no invented stages, and a lost stream is "reconciling", not "failed".
 */
export type Phase = "idle" | "sending" | "streaming" | "stopping" | "reconciling" | "finished";

export type NoticeKind = "unsent" | "busy" | "error" | "unconfirmed";

export interface Notice {
  kind: NoticeKind;
  text: string;
}

export interface RunState {
  phase: Phase;
  conversationId: string;
  runId: string | null;
  /** The question being answered, shown at once. */
  message: string | null;
  content: string;
  thinking: string;
  citations: Citation[];
  startedAt: number | null;
  terminal: RunSnapshot | null;
  notice: Notice | null;
  /** Text to put back into the composer when the message never reached the server. */
  unsentMessage: string | null;
  /** A delta arrived out of order; the stored reply is the source of truth once the run ends. */
  gap: boolean;
}

export type RunAction =
  | { type: "send"; runId: string; message: string; now: number }
  | { type: "run"; run: RunSnapshot }
  | { type: "delta"; delta: DeltaEvent }
  | { type: "citations"; items: Citation[] }
  | { type: "done"; run: RunSnapshot }
  | { type: "streamEnded" }
  | { type: "networkFailed"; started: boolean }
  | { type: "apiFailed"; status: number; error: Pick<RunError, "code" | "message" | "retryable"> }
  | { type: "stopClicked" }
  | { type: "reconciled"; run: RunSnapshot | null }
  | { type: "reconcileGaveUp" }
  | { type: "unsentRestored" }
  | { type: "reset"; conversationId: string };

export function initialRunState(conversationId: string): RunState {
  return {
    phase: "idle",
    conversationId,
    runId: null,
    message: null,
    content: "",
    thinking: "",
    citations: [],
    startedAt: null,
    terminal: null,
    notice: null,
    unsentMessage: null,
    gap: false
  };
}

export const canSend = (state: RunState): boolean => state.phase === "idle" || state.phase === "finished";
export const isActive = (state: RunState): boolean =>
  state.phase === "sending" || state.phase === "streaming" || state.phase === "stopping" || state.phase === "reconciling";

const NOT_DELIVERED = "消息没有送达服务器，已放回输入框，可以再次发送。";

function backToComposer(state: RunState, text: string): RunState {
  return {
    ...state,
    phase: "idle",
    runId: null,
    unsentMessage: state.message,
    message: null,
    notice: { kind: "unsent", text }
  };
}

export function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case "send":
      // A second click while a run is active is ignored: the server would also refuse it.
      if (!canSend(state)) return state;
      return {
        ...initialRunState(state.conversationId),
        phase: "sending",
        runId: action.runId,
        message: action.message,
        startedAt: action.now
      };

    case "run":
      if (state.phase !== "sending" && state.phase !== "streaming") return state;
      return { ...state, phase: "streaming" };

    case "delta": {
      if (state.phase !== "sending" && state.phase !== "streaming" && state.phase !== "stopping") return state;
      const { offset, content, thinking } = action.delta;
      let next = state;
      if (content) {
        if (offset === state.content.length) next = { ...next, content: state.content + content };
        else if (offset > state.content.length) next = { ...next, gap: true };
      }
      if (thinking) {
        if (offset === state.thinking.length) next = { ...next, thinking: state.thinking + thinking };
        else if (offset > state.thinking.length) next = { ...next, gap: true };
      }
      return next;
    }

    case "citations":
      return isActive(state) ? { ...state, citations: [...state.citations, ...action.items] } : state;

    case "done":
      if (state.phase === "idle" || state.phase === "finished") return state;
      return { ...state, phase: "finished", terminal: action.run, runId: action.run.runId };

    case "streamEnded":
      if (state.phase === "sending") return backToComposer(state, NOT_DELIVERED);
      if (state.phase === "streaming" || state.phase === "stopping") return { ...state, phase: "reconciling" };
      return state;

    case "networkFailed":
      if (state.phase === "sending" || state.phase === "streaming" || state.phase === "stopping") {
        return action.started ? { ...state, phase: "reconciling" } : backToComposer(state, NOT_DELIVERED);
      }
      return state;

    case "apiFailed": {
      if (state.phase !== "sending") return state;
      if (action.error.code === "run_in_progress") {
        return { ...backToComposer(state, action.error.message), notice: { kind: "busy", text: action.error.message } };
      }
      return action.error.retryable
        ? backToComposer(state, action.error.message)
        : { ...backToComposer(state, action.error.message), notice: { kind: "error", text: action.error.message } };
    }

    case "stopClicked":
      return state.phase === "streaming" ? { ...state, phase: "stopping" } : state;

    case "reconciled":
      if (state.phase !== "reconciling") return state;
      if (action.run === null) return backToComposer(state, NOT_DELIVERED);
      if (action.run.status === "running") return state;
      return { ...state, phase: "finished", terminal: action.run };

    case "reconcileGaveUp":
      if (state.phase !== "reconciling") return state;
      return {
        ...state,
        phase: "finished",
        notice: { kind: "unconfirmed", text: "暂时无法确认这次生成的结果。刷新页面后会再次同步。" }
      };

    case "unsentRestored":
      return { ...state, unsentMessage: null };

    case "reset":
      return initialRunState(action.conversationId);
  }
}
