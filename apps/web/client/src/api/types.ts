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
  citations?: Citation[];
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
  providerId?: string;
  modelName: string;
  updatedAt: string;
}

export interface ConversationPage {
  items: ConversationSummary[];
  hasMore: boolean;
  nextCursor: string | null;
}

/** Per-run options, in the shape the v2 run endpoint takes. Only what the chosen model supports is ever set. */
export interface RunOptions {
  providerId?: string;
  modelName?: string;
  presetId?: string;
  customPrompt?: string | null;
  searchMode?: SearchMode;
  thinkingEnabled?: boolean | null;
  thinkingLevel?: string | null;
  thinkingBudget?: number | null;
  advanced?: AdvancedOptions;
}

export type SearchMode = "off" | "auto" | "force";

export interface AdvancedOptions {
  temperature?: number;
  topP?: number;
  maxOutputTokens?: number;
  safetyThreshold?: string;
}

export interface CreateRunRequest extends RunOptions {
  runId: string;
  conversationId: string;
  message: string;
}

export interface ThinkingOption {
  value: string;
  label: string;
}

export interface ThinkingConfig {
  kind: string;
  canDisable: boolean;
  fixedEnabled: boolean;
  includeThoughts: boolean;
  default?: string | null;
  options: ThinkingOption[];
  budgets: number[];
  defaultBudget?: number | null;
}

export interface ModelOption {
  name: string;
  modelName: string;
  description: string;
  supportsThinking: boolean;
  supportsSampling: boolean;
  thinking: ThinkingConfig | null;
  maxTokens: number;
}

export interface PromptPreset {
  id: string;
  name: string;
  prompt: string;
  description: string;
}

export interface WorkspaceProvider {
  provider: { id: string; name: string; description: string };
  models: ModelOption[];
  presets: PromptPreset[];
  defaultPresetId: string;
  defaultModelName: string;
}

export interface WorkspaceConfig {
  providers: WorkspaceProvider[];
  defaultProviderId: string;
}

export interface AuthStatus {
  email: string;
  safetyThresholds: string[];
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
  listConversations(cursor?: string | null): Promise<ConversationPage>;
  getWorkspace(): Promise<WorkspaceConfig>;
  getAuthStatus(): Promise<AuthStatus>;
}
