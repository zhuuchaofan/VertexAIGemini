export type RunStatus = "running" | "completed" | "stopped" | "interrupted" | "failed";

export interface RunError {
  code: string;
  message: string;
  retryable: boolean;
}

/** What the server tells clients about a run. */
export interface RunSnapshot {
  runId: string;
  conversationId: string;
  userMessageId: string;
  assistantMessageId: string;
  status: RunStatus;
  statusReason: string | null;
  attempt: number;
  error: RunError | null;
  outputChars: number;
  hasAssistantMessage: boolean;
  providerCallStarted: boolean;
  admittedAt: string | null;
  endedAt: string | null;
  replayed?: boolean;
}

export interface DeltaEvent {
  offset: number;
  content?: string | null;
  thinking?: string | null;
}

export interface Citation {
  title: string;
  uri: string;
}

export interface StoredMessage {
  id: string;
  role: "user" | "model";
  content: string;
  thinkingContent: string | null;
  runId: string | null;
  status: "final" | "partial" | null;
  createdAt: string;
}

export interface ConversationDetail {
  id: string;
  title: string | null;
  providerId: string;
  modelName: string;
  activeRunId: string | null;
  messages: StoredMessage[];
  runs: RunSnapshot[];
}

export interface ConversationSummary {
  id: string;
  title: string | null;
  modelName: string;
  updatedAt: string;
}

export interface CreateRunRequest {
  runId: string;
  conversationId: string;
  message: string;
}

export interface RunStreamHandlers {
  onRun: (run: RunSnapshot) => void;
  onDelta: (delta: DeltaEvent) => void;
  onCitations: (items: Citation[]) => void;
  onDone: (run: RunSnapshot) => void;
}

export interface RunStreamResult {
  /** True once the server acknowledged the run (the first event arrived). */
  started: boolean;
  /** True if the stream delivered a terminal snapshot. */
  done: boolean;
}

/** The server's JSON error body, as an exception. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly retryable: boolean,
    readonly extra?: unknown
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** A failure to reach the server at all (offline, reset), as opposed to an answer from it. */
export class NetworkError extends Error {
  constructor(message: string, readonly started: boolean) {
    super(message);
    this.name = "NetworkError";
  }
}

export interface RunApi {
  createRun(request: CreateRunRequest, handlers: RunStreamHandlers, signal: AbortSignal): Promise<RunStreamResult>;
  stopRun(runId: string): Promise<RunSnapshot | null>;
  getRun(runId: string): Promise<RunSnapshot | null>;
  getConversation(conversationId: string): Promise<ConversationDetail | null>;
  listConversations(): Promise<ConversationSummary[]>;
}
