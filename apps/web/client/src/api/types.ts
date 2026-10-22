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
  /** The failed run this one tries again, and the run that tried this one again. */
  retryOfRunId?: string | null;
  retriedByRunId?: string | null;
  requested?: RunOptions | null;
  effective?: RunEffective | null;
  usage?: RunUsage | null;
  finishReason?: string | null;
  modelVersion?: string | null;
  responseId?: string | null;
  providerCalls?: { generate: number; summarize: number; countTokens: number; search?: number } | null;
  /** What happened about the web for a provider with no search of its own. Absent for providers that search natively. */
  search?: RunSearch | null;
  /** Server-side times. Null where the run never got that far. */
  timing?: { createdAt?: string | null; startedAt?: string | null; firstOutputAt?: string | null; endedAt?: string | null } | null;
  output?: { chars: number; thinkingChars: number; citations: number } | null;
}

/** `status`: used, skipped (no web needed), failed, or unavailable. */
export interface RunSearch {
  status: string;
  query?: string | null;
  sources: number;
  reason?: string | null;
}

/** What the server applied to the request. A field the model does not take is null. */
export interface RunEffective {
  providerId: string;
  modelName: string;
  presetId: string;
  searchMode: string;
  thinkingEnabled?: boolean | null;
  thinkingLevel?: string | null;
  thinkingBudget?: number | null;
  temperature?: number | null;
  topP?: number | null;
  maxOutputTokens?: number | null;
  safetyThreshold?: string | null;
}

/** Tokens as far as known; a missing figure is unknown. `source` is "provider" or "estimate". */
export interface RunUsage {
  inputTokens?: number | null;
  outputTokens?: number | null;
  thoughtsTokens?: number | null;
  totalTokens?: number | null;
  source: string;
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

/** What the browser is told about an attachment. The content is fetched separately, one attachment at a time. */
export interface AttachmentView {
  index: number;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  /** "pending" while its run is still going, "failed" when it never reached storage. */
  state: "pending" | "stored" | "failed";
}

export interface OutgoingAttachment {
  base64Data: string;
  mimeType: string;
  fileName: string;
}

export interface StoredMessage {
  id: string;
  role: "user" | "model";
  content: string;
  thinkingContent: string | null;
  runId: string | null;
  status: "final" | "partial" | null;
  citations?: Citation[];
  attachments?: AttachmentView[];
  createdAt: string;
}

export interface ConversationDetail {
  id: string;
  title: string | null;
  providerId: string;
  modelName: string;
  activeRunId: string | null;
  settings?: StoredSettings | null;
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
  attachments?: OutgoingAttachment[];
  /** Answers the stored question of this failed run instead of sending a new message. */
  retryOfRunId?: string;
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

/** A settings layer as the server stores it. Fields can be missing on older conversations. */
export interface StoredSettings {
  providerId: string;
  modelName: string;
  presetId?: string | null;
  customPrompt?: string | null;
  searchMode?: SearchMode | null;
  thinking?: { level?: string | null; enabled?: boolean | null; budget?: number | null } | null;
  advanced?: AdvancedOptions | null;
}

/** A named system prompt kept in the person's own account. */
export interface UserRole {
  id: string;
  name: string;
  prompt: string;
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
  getAttachment(conversationId: string, messageId: string, index: number): Promise<Blob>;
  renameConversation(conversationId: string, title: string): Promise<void>;
  deleteConversation(conversationId: string): Promise<void>;
  exportConversation(conversationId: string, format: "markdown" | "json"): Promise<{ blob: Blob; fileName: string }>;
  getDefaults(): Promise<StoredSettings | null>;
  putDefaults(settings: StoredSettings): Promise<StoredSettings | null>;
  putConversationSettings(conversationId: string, settings: StoredSettings): Promise<void>;
  listRoles(): Promise<{ roles: UserRole[]; maxRoles: number; defaultRoleId: string | null }>;
  /** The role a new conversation starts with; null clears it. */
  setDefaultRole(roleId: string | null): Promise<void>;
  createRole(role: { name: string; prompt: string }): Promise<UserRole>;
  updateRole(id: string, role: { name: string; prompt: string }): Promise<UserRole>;
  deleteRole(id: string): Promise<void>;
}
