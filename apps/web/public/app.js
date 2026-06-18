const mobileQuery = window.matchMedia("(max-width: 820px)");
const savedSidebarState = localStorage.getItem("vertex-sidebar-collapsed");
const WORKSPACE_PREFERENCES_KEY = "vertex-workspace-preferences";
const workspacePreferences = loadWorkspacePreferences();

const state = {
  mode: "login",
  user: null,
  conversationId: null,
  conversations: [],
  conversationOffset: 0,
  conversationPageSize: 30,
  conversationsHasMore: false,
  conversationsLoading: false,
  loadedConversationMessages: [],
  visibleMessageStart: 0,
  messagePageSize: 80,
  workspaceConfig: null,
  userSettings: null,
  firebaseAuth: null,
  firebaseSdk: null,
  firebaseAuthReady: false,
  pendingImages: [],
  streaming: false,
  sidebarCollapsed: mobileQuery.matches ? true : savedSidebarState === "true"
};

const MAX_IMAGE_BYTES = 4 * 1024 * 1024;
const STREAM_RENDER_INTERVAL_MS = 80;

const elements = {
  appLoading: document.querySelector("#app-loading"),
  authScreen: document.querySelector("#auth-screen"),
  workspaceShell: document.querySelector("#workspace-shell"),
  collapseSidebar: document.querySelector("#collapse-sidebar"),
  expandSidebar: document.querySelector("#expand-sidebar"),
  loginTab: document.querySelector("#login-tab"),
  registerTab: document.querySelector("#register-tab"),
  authForm: document.querySelector("#auth-form"),
  authSubmit: document.querySelector("#auth-submit"),
  authMessage: document.querySelector("#auth-message"),
  logoutButton: document.querySelector("#logout-button"),
  settingsButton: document.querySelector("#settings-button"),
  settingsModal: document.querySelector("#settings-modal"),
  settingsClose: document.querySelector("#settings-close"),
  settingsForm: document.querySelector("#settings-form"),
  settingsReset: document.querySelector("#settings-reset"),
  defaultAssistantPrompt: document.querySelector("#default-assistant-prompt"),
  settingsMessage: document.querySelector("#settings-message"),
  sessionLabel: document.querySelector("#session-label"),
  connectionState: document.querySelector("#connection-state"),
  providerSelect: document.querySelector("#provider-select"),
  modelSelect: document.querySelector("#model-select"),
  thinkingSettings: document.querySelector("#thinking-settings"),
  presetSelect: document.querySelector("#preset-select"),
  customPromptLabel: document.querySelector("#custom-prompt-label"),
  customPrompt: document.querySelector("#custom-prompt"),
  enableSearch: document.querySelector("#enable-search"),
  newChat: document.querySelector("#new-chat"),
  refreshConversations: document.querySelector("#refresh-conversations"),
  conversationList: document.querySelector("#conversation-list"),
  loadMoreConversations: document.querySelector("#load-more-conversations"),
  conversationEmpty: document.querySelector("#conversation-empty"),
  chatForm: document.querySelector("#chat-form"),
  messageInput: document.querySelector("#message-input"),
  imageInput: document.querySelector("#image-input"),
  attachmentPreview: document.querySelector("#attachment-preview"),
  sendButton: document.querySelector("#send-button"),
  messages: document.querySelector("#messages"),
  template: document.querySelector("#message-template")
};

elements.collapseSidebar.addEventListener("click", () => setSidebarCollapsed(true));
elements.expandSidebar.addEventListener("click", () => setSidebarCollapsed(false));
elements.loginTab.addEventListener("click", () => setAuthMode("login"));
elements.registerTab.addEventListener("click", () => setAuthMode("register"));
elements.authForm.addEventListener("submit", submitAuth);
elements.logoutButton.addEventListener("click", logout);
elements.settingsButton.addEventListener("click", openSettings);
elements.settingsClose.addEventListener("click", closeSettings);
elements.settingsModal.addEventListener("click", event => {
  if (event.target === elements.settingsModal) {
    closeSettings();
  }
});
elements.settingsForm.addEventListener("submit", saveUserSettings);
elements.settingsReset.addEventListener("click", resetUserSettings);
elements.newChat.addEventListener("click", () => {
  startNewChat();
  collapseSidebarOnMobile();
});
elements.refreshConversations.addEventListener("click", () => refreshConversations());
elements.loadMoreConversations.addEventListener("click", () => loadMoreConversations());
elements.conversationList.addEventListener("click", handleConversationClick);
elements.conversationList.addEventListener("keydown", handleConversationKeydown);
elements.providerSelect.addEventListener("change", () => {
  saveWorkspacePreference("providerId", elements.providerSelect.value);
  renderProviderCatalog();
});
elements.modelSelect.addEventListener("change", () => {
  saveSelectedModelPreference();
  renderThinkingSettings();
});
elements.presetSelect.addEventListener("change", () => {
  saveSelectedPresetPreference();
  renderCustomPromptState();
});
elements.customPrompt.addEventListener("input", () => {
  saveWorkspacePreference("customPrompt", elements.customPrompt.value);
});
elements.enableSearch.addEventListener("change", () => {
  saveWorkspacePreference("enableSearch", elements.enableSearch.checked);
});
elements.chatForm.addEventListener("submit", sendMessage);
elements.messages.addEventListener("click", handleMessagesClick);
elements.imageInput.addEventListener("change", handleImageSelection);
elements.attachmentPreview.addEventListener("click", removePendingImage);
elements.messageInput.addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    elements.chatForm.requestSubmit();
  }
});
elements.messageInput.addEventListener("input", resizeComposer);

renderSidebarState();
await loadWorkspaceConfig();
await initializeFirebaseAuth();
await refreshSession();
startNewChat();

function setSidebarCollapsed(collapsed) {
  state.sidebarCollapsed = collapsed;
  localStorage.setItem("vertex-sidebar-collapsed", String(collapsed));
  renderSidebarState();
}

function renderSidebarState() {
  elements.workspaceShell.classList.toggle("sidebar-collapsed", state.sidebarCollapsed);
  elements.collapseSidebar.setAttribute("aria-expanded", String(!state.sidebarCollapsed));
  elements.expandSidebar.setAttribute("aria-expanded", String(!state.sidebarCollapsed));
}

function collapseSidebarOnMobile() {
  if (mobileQuery.matches) {
    setSidebarCollapsed(true);
  }
}

function setAuthMode(mode) {
  state.mode = mode;
  elements.loginTab.classList.toggle("active", mode === "login");
  elements.registerTab.classList.toggle("active", mode === "register");
  elements.authSubmit.textContent = mode === "login" ? "登录" : "注册";
}

async function initializeFirebaseAuth() {
  const firebase = state.workspaceConfig?.firebase;
  if (!firebase?.apiKey || !firebase?.projectId) {
    setAuthMessage("Firebase 配置缺失，无法登录");
    setConnection("error", "认证配置缺失");
    return;
  }

  const [appModule, authModule] = await Promise.all([
    import("https://www.gstatic.com/firebasejs/11.10.0/firebase-app.js"),
    import("https://www.gstatic.com/firebasejs/11.10.0/firebase-auth.js")
  ]);

  const app = appModule.initializeApp({
    apiKey: firebase.apiKey,
    authDomain: firebase.authDomain || `${firebase.projectId}.firebaseapp.com`,
    projectId: firebase.projectId,
    appId: firebase.appId || undefined
  });

  state.firebaseSdk = authModule;
  state.firebaseAuth = authModule.getAuth(app);
}

function waitForFirebaseAuth() {
  if (!state.firebaseAuth || state.firebaseAuthReady) {
    return Promise.resolve();
  }

  return new Promise(resolve => {
    const unsubscribe = state.firebaseSdk.onAuthStateChanged(state.firebaseAuth, () => {
      state.firebaseAuthReady = true;
      unsubscribe();
      resolve();
    });
  });
}

function toFirebaseUserInfo(user) {
  if (!user) return null;

  return {
    id: user.uid,
    email: user.email ?? "Firebase user",
    emailVerified: user.emailVerified
  };
}

async function submitAuth(event) {
  event.preventDefault();
  const form = new FormData(elements.authForm);
  const payload = {
    email: String(form.get("email") ?? ""),
    password: String(form.get("password") ?? "")
  };

  setAuthMessage("正在处理...");

  if (!state.firebaseAuth || !state.firebaseSdk) {
    setAuthMessage("Firebase 认证未初始化");
    setConnection("error", "认证配置缺失");
    return;
  }

  try {
    const credential = state.mode === "login"
      ? await state.firebaseSdk.signInWithEmailAndPassword(state.firebaseAuth, payload.email, payload.password)
      : await state.firebaseSdk.createUserWithEmailAndPassword(state.firebaseAuth, payload.email, payload.password);

    state.user = toFirebaseUserInfo(credential.user);
    renderSession();
    await loadUserSettings();
    await refreshConversations();
    setAuthMessage(state.mode === "login" ? "已登录" : "已注册并登录");
    setConnection("ready", "API 已连接");
  } catch (error) {
    setAuthMessage(toAuthErrorMessage(error));
    setConnection("error", "认证失败");
  }
}

async function logout() {
  if (state.firebaseAuth) {
    await state.firebaseSdk.signOut(state.firebaseAuth);
  }

  state.user = null;
  state.conversationId = null;
  state.conversations = [];
  state.conversationOffset = 0;
  state.conversationsHasMore = false;
  state.userSettings = null;
  renderSession();
  renderConversations();
  startNewChat();
  closeSettings();
  setAuthMessage("已退出登录");
  setConnection("", "API 未连接");
}

async function refreshSession() {
  await waitForFirebaseAuth();
  state.user = toFirebaseUserInfo(state.firebaseAuth?.currentUser);
  if (state.user) {
    setConnection("ready", "API 已连接");
    await loadUserSettings();
    await refreshConversations();
  }

  renderSession();
}

function renderSession() {
  elements.appLoading.classList.add("hidden");
  elements.authForm.classList.remove("hidden");

  if (state.user) {
    elements.sessionLabel.textContent = state.user.email;
    elements.logoutButton.classList.remove("hidden");
    elements.authScreen.classList.add("hidden");
    elements.workspaceShell.classList.remove("hidden");
  } else {
    elements.sessionLabel.textContent = "未登录";
    elements.logoutButton.classList.add("hidden");
    elements.authScreen.classList.remove("hidden");
    elements.workspaceShell.classList.add("hidden");
  }
}

async function loadUserSettings() {
  if (!state.user) return null;

  const response = await apiFetch("/api/user/settings/");
  const settings = await readJson(response);
  if (!response.ok || !settings) {
    return null;
  }

  state.userSettings = settings;
  return settings;
}

async function openSettings() {
  if (!state.user) return;

  const settings = state.userSettings ?? await loadUserSettings();
  renderSettings(settings);
  elements.settingsModal.classList.remove("hidden");
  elements.defaultAssistantPrompt.focus();
}

function closeSettings() {
  elements.settingsModal.classList.add("hidden");
  setSettingsMessage("");
}

function renderSettings(settings) {
  const fallbackPrompt = settings?.systemDefaultAssistantPrompt ?? "";
  elements.defaultAssistantPrompt.value = settings?.defaultAssistantPrompt ?? fallbackPrompt;
  setSettingsMessage("");
}

async function saveUserSettings(event) {
  event.preventDefault();
  try {
    await updateUserSettings(elements.defaultAssistantPrompt.value);
    setSettingsMessage("已保存");
  } catch {
    // Message is already rendered by updateUserSettings.
  }
}

async function resetUserSettings() {
  try {
    const settings = await updateUserSettings(null);
    renderSettings(settings);
    setSettingsMessage("已恢复系统默认");
  } catch {
    // Message is already rendered by updateUserSettings.
  }
}

async function updateUserSettings(defaultAssistantPrompt) {
  const response = await apiFetch("/api/user/settings/", {
    method: "PATCH",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ defaultAssistantPrompt })
  });
  const settings = await readJson(response);

  if (!response.ok || !settings) {
    setSettingsMessage(settings?.error ?? "保存失败");
    throw new Error(settings?.error ?? "保存失败");
  }

  state.userSettings = settings;
  return settings;
}

function startNewChat() {
  state.conversationId = null;
  state.loadedConversationMessages = [];
  state.visibleMessageStart = 0;
  clearPendingImages();
  renderConversations();
  elements.messages.replaceChildren();
  if (state.user) {
    elements.messageInput.focus();
  }
}

async function loadWorkspaceConfig() {
  const response = await apiFetch("/api/workspace/config");
  const config = await readJson(response);
  if (!response.ok || !config) {
    setConnection("error", "配置加载失败");
    return;
  }

  state.workspaceConfig = config;
  renderWorkspaceConfig();
}

function renderWorkspaceConfig() {
  elements.providerSelect.replaceChildren();

  for (const provider of state.workspaceConfig?.providers ?? []) {
    const option = document.createElement("option");
    option.value = provider.provider.id;
    option.textContent = provider.provider.name;
    option.title = provider.provider.description;
    elements.providerSelect.append(option);
  }

  if (!setSelectValue(elements.providerSelect, workspacePreferences.providerId)) {
    setSelectValue(elements.providerSelect, state.workspaceConfig?.defaultProviderId);
  }
  elements.enableSearch.checked = Boolean(workspacePreferences.enableSearch);
  elements.customPrompt.value = workspacePreferences.customPrompt ?? "";

  renderProviderCatalog();
}

function renderProviderCatalog() {
  elements.modelSelect.replaceChildren();
  elements.presetSelect.replaceChildren();

  const providerConfig = getSelectedProviderConfig();

  for (const model of providerConfig?.models ?? []) {
    const option = document.createElement("option");
    option.value = model.modelName;
    option.textContent = model.name;
    option.title = model.description;
    elements.modelSelect.append(option);
  }

  for (const preset of providerConfig?.presets ?? []) {
    const option = document.createElement("option");
    option.value = preset.id;
    option.textContent = preset.name;
    option.title = preset.description;
    elements.presetSelect.append(option);
  }

  const providerId = providerConfig?.provider?.id;
  if (!setSelectValue(elements.modelSelect, workspacePreferences.modelByProvider?.[providerId])) {
    setSelectValue(elements.modelSelect, providerConfig?.defaultModelName);
  }
  if (!setSelectValue(elements.presetSelect, workspacePreferences.presetByProvider?.[providerId])) {
    setSelectValue(elements.presetSelect, providerConfig?.defaultPresetId);
  }

  renderThinkingSettings();
  renderCustomPromptState();
}

function getSelectedProviderConfig() {
  const selected = elements.providerSelect.value || state.workspaceConfig?.defaultProviderId;
  return state.workspaceConfig?.providers?.find(item => item.provider.id === selected)
    ?? state.workspaceConfig?.providers?.[0]
    ?? null;
}

function renderCustomPromptState() {
  elements.customPromptLabel.classList.toggle("hidden", elements.presetSelect.value !== "custom");
}

function getSelectedModel() {
  const providerConfig = getSelectedProviderConfig();
  const selected = elements.modelSelect.value || providerConfig?.defaultModelName;
  return providerConfig?.models?.find(model => model.modelName === selected) ?? null;
}

function loadWorkspacePreferences() {
  try {
    return JSON.parse(localStorage.getItem(WORKSPACE_PREFERENCES_KEY) ?? "{}") ?? {};
  } catch {
    return {};
  }
}

function saveWorkspacePreferences() {
  localStorage.setItem(WORKSPACE_PREFERENCES_KEY, JSON.stringify(workspacePreferences));
}

function saveWorkspacePreference(key, value) {
  workspacePreferences[key] = value;
  saveWorkspacePreferences();
}

function saveSelectedModelPreference() {
  const providerId = getSelectedProviderConfig()?.provider?.id;
  if (!providerId || !elements.modelSelect.value) return;

  workspacePreferences.modelByProvider ??= {};
  workspacePreferences.modelByProvider[providerId] = elements.modelSelect.value;
  saveWorkspacePreferences();
}

function saveSelectedPresetPreference() {
  const providerId = getSelectedProviderConfig()?.provider?.id;
  if (!providerId || !elements.presetSelect.value) return;

  workspacePreferences.presetByProvider ??= {};
  workspacePreferences.presetByProvider[providerId] = elements.presetSelect.value;
  saveWorkspacePreferences();
}

function getThinkingPreferenceKey() {
  const providerId = getSelectedProviderConfig()?.provider?.id;
  const modelName = elements.modelSelect.value;
  return providerId && modelName ? `${providerId}:${modelName}` : null;
}

function getSavedThinkingPreference() {
  const key = getThinkingPreferenceKey();
  return key ? workspacePreferences.thinkingByModel?.[key] : null;
}

function saveThinkingPreference() {
  const key = getThinkingPreferenceKey();
  if (!key) return;

  const thinking = getSelectedModel()?.thinking;
  if (!thinking) return;

  let value;
  if (thinking.kind === "qwen-budget") {
    value = {
      enabled: elements.thinkingSettings.querySelector("#thinking-enabled")?.checked ?? true,
      budget: elements.thinkingSettings.querySelector("#thinking-budget-select")?.value ?? "custom",
      customBudget: elements.thinkingSettings.querySelector("#thinking-budget-custom")?.value ?? ""
    };
  } else {
    value = {
      level: elements.thinkingSettings.querySelector("#thinking-level")?.value ?? thinking.default ?? "on"
    };
  }

  workspacePreferences.thinkingByModel ??= {};
  workspacePreferences.thinkingByModel[key] = value;
  saveWorkspacePreferences();
}

function renderThinkingSettings() {
  const thinking = getSelectedModel()?.thinking;
  elements.thinkingSettings.replaceChildren();

  if (!thinking) {
    elements.thinkingSettings.classList.add("hidden");
    return;
  }

  elements.thinkingSettings.classList.remove("hidden");

  const heading = document.createElement("div");
  heading.className = "thinking-heading";
  heading.textContent = "思考设置";
  elements.thinkingSettings.append(heading);

  const hasChoices = (thinking.options?.length ?? 0) > 0 || (thinking.budgets?.length ?? 0) > 0;
  if (thinking.fixedEnabled && !hasChoices) {
    const readonly = document.createElement("div");
    readonly.className = "thinking-readonly";
    readonly.textContent = "始终开启";
    elements.thinkingSettings.append(readonly);
    return;
  }

  if (thinking.kind === "qwen-budget") {
    renderBudgetThinking(thinking);
    return;
  }

  renderLevelThinking(thinking);
}

function renderLevelThinking(thinking) {
  const label = document.createElement("label");
  label.textContent = "级别";

  const select = document.createElement("select");
  select.id = "thinking-level";

  const options = thinking.options?.length
    ? thinking.options
    : [
        { value: "off", label: "关闭" },
        { value: "on", label: "开启" }
      ];

  for (const option of options) {
    const node = document.createElement("option");
    node.value = option.value;
    node.textContent = option.label || option.value;
    select.append(node);
  }

  const saved = getSavedThinkingPreference();
  setSelectValue(select, saved?.level ?? thinking.default ?? options[0]?.value ?? "on");
  select.addEventListener("change", saveThinkingPreference);
  label.append(select);
  elements.thinkingSettings.append(label);
}

function renderBudgetThinking(thinking) {
  const enabledLabel = document.createElement("label");
  enabledLabel.className = "toggle-row";

  const enabled = document.createElement("input");
  enabled.id = "thinking-enabled";
  enabled.type = "checkbox";
  const saved = getSavedThinkingPreference();
  enabled.checked = saved?.enabled ?? thinking.default !== "off";

  const enabledText = document.createElement("span");
  enabledText.textContent = "开启思考";
  enabledLabel.append(enabled, enabledText);

  const row = document.createElement("div");
  row.className = "thinking-row two-columns";

  const budgetLabel = document.createElement("label");
  budgetLabel.textContent = "预算";

  const budgetSelect = document.createElement("select");
  budgetSelect.id = "thinking-budget-select";

  for (const budget of thinking.budgets ?? []) {
    const option = document.createElement("option");
    option.value = String(budget);
    option.textContent = String(budget);
    budgetSelect.append(option);
  }

  const customOption = document.createElement("option");
  customOption.value = "custom";
  customOption.textContent = "自定义";
  budgetSelect.append(customOption);

  const customLabel = document.createElement("label");
  customLabel.textContent = "自定义";
  const customInput = document.createElement("input");
  customInput.id = "thinking-budget-custom";
  customInput.type = "number";
  customInput.min = "1";
  customInput.step = "1";
  customInput.value = String(saved?.customBudget ?? thinking.defaultBudget ?? thinking.budgets?.[0] ?? 500);
  customLabel.append(customInput);

  const defaultBudget = String(saved?.budget ?? thinking.defaultBudget ?? thinking.budgets?.[0] ?? "custom");
  budgetSelect.value = [...budgetSelect.options].some(option => option.value === defaultBudget)
    ? defaultBudget
    : "custom";

  const updateBudgetState = () => {
    const disabled = !enabled.checked;
    budgetSelect.disabled = disabled;
    customInput.disabled = disabled || budgetSelect.value !== "custom";
    customLabel.classList.toggle("hidden", budgetSelect.value !== "custom");
  };

  enabled.addEventListener("change", () => {
    updateBudgetState();
    saveThinkingPreference();
  });
  budgetSelect.addEventListener("change", () => {
    updateBudgetState();
    saveThinkingPreference();
  });
  customInput.addEventListener("input", saveThinkingPreference);

  budgetLabel.append(budgetSelect);
  row.append(budgetLabel, customLabel);
  elements.thinkingSettings.append(enabledLabel, row);
  updateBudgetState();
}

function getThinkingPayload() {
  const thinking = getSelectedModel()?.thinking;
  if (!thinking) {
    return {};
  }

  const hasChoices = (thinking.options?.length ?? 0) > 0 || (thinking.budgets?.length ?? 0) > 0;
  if (thinking.fixedEnabled && !hasChoices) {
    return { thinkingEnabled: true };
  }

  if (thinking.kind === "qwen-budget") {
    const enabled = elements.thinkingSettings.querySelector("#thinking-enabled")?.checked ?? true;
    const budgetSelect = elements.thinkingSettings.querySelector("#thinking-budget-select");
    const customBudget = elements.thinkingSettings.querySelector("#thinking-budget-custom");
    const budget = budgetSelect?.value === "custom"
      ? Number(customBudget?.value)
      : Number(budgetSelect?.value);

    return {
      thinkingEnabled: enabled,
      thinkingBudget: enabled && Number.isFinite(budget) && budget > 0 ? Math.floor(budget) : null
    };
  }

  const level = elements.thinkingSettings.querySelector("#thinking-level")?.value ?? thinking.default ?? "on";
  return {
    thinkingEnabled: level !== "off",
    thinkingLevel: level
  };
}

async function refreshConversations() {
  if (!state.user) {
    state.conversations = [];
    state.conversationOffset = 0;
    state.conversationsHasMore = false;
    renderConversations();
    return;
  }

  state.conversationOffset = 0;
  await fetchConversationsPage(false);
}

async function loadMoreConversations() {
  if (!state.user || state.conversationsLoading || !state.conversationsHasMore) {
    return;
  }

  await fetchConversationsPage(true);
}

async function fetchConversationsPage(append) {
  state.conversationsLoading = true;
  elements.loadMoreConversations.disabled = true;

  const params = new URLSearchParams({
    offset: String(append ? state.conversationOffset : 0),
    limit: String(state.conversationPageSize)
  });
  const response = await apiFetch(`/api/conversations/?${params}`);
  if (!response.ok) {
    elements.conversationEmpty.textContent = "无法加载会话";
    elements.conversationEmpty.classList.remove("hidden");
    state.conversationsLoading = false;
    elements.loadMoreConversations.disabled = false;
    return;
  }

  const page = await response.json();
  const items = Array.isArray(page) ? page : page.items ?? [];
  state.conversations = append ? [...state.conversations, ...items] : items;
  state.conversationOffset = state.conversations.length;
  state.conversationsHasMore = Array.isArray(page)
    ? items.length === state.conversationPageSize
    : Boolean(page.hasMore);
  state.conversationsLoading = false;
  renderConversations();
}

function renderConversations() {
  elements.conversationList.replaceChildren();
  elements.loadMoreConversations.classList.add("hidden");
  elements.loadMoreConversations.disabled = state.conversationsLoading;

  if (!state.user) {
    elements.conversationEmpty.textContent = "登录后显示历史会话";
    elements.conversationEmpty.classList.remove("hidden");
    return;
  }

  if (state.conversations.length === 0) {
    elements.conversationEmpty.textContent = "暂无历史会话";
    elements.conversationEmpty.classList.remove("hidden");
    return;
  }

  elements.conversationEmpty.classList.add("hidden");
  for (const conversation of state.conversations) {
    elements.conversationList.append(createConversationItem(conversation));
  }

  elements.loadMoreConversations.classList.toggle("hidden", !state.conversationsHasMore);
  elements.loadMoreConversations.textContent = state.conversationsLoading ? "加载中..." : "加载更多";
}

function createConversationItem(conversation) {
  const row = document.createElement("div");
  row.className = "conversation-item";
  row.dataset.action = "load";
  row.dataset.id = conversation.id;
  row.role = "button";
  row.tabIndex = 0;
  row.classList.toggle("active", conversation.id === state.conversationId);

  const title = document.createElement("span");
  title.className = "conversation-title";
  title.textContent = conversation.title || "未命名对话";

  const meta = document.createElement("span");
  meta.className = "conversation-meta";
  meta.textContent = formatConversationMeta(conversation);

  const renameButton = document.createElement("button");
  renameButton.type = "button";
  renameButton.className = "conversation-action conversation-rename";
  renameButton.dataset.action = "rename";
  renameButton.dataset.id = conversation.id;
  renameButton.setAttribute("aria-label", "重命名会话");
  renameButton.title = "重命名会话";
  renameButton.textContent = "✎";

  const exportMarkdownButton = document.createElement("button");
  exportMarkdownButton.type = "button";
  exportMarkdownButton.className = "conversation-action conversation-export-md";
  exportMarkdownButton.dataset.action = "export-markdown";
  exportMarkdownButton.dataset.id = conversation.id;
  exportMarkdownButton.setAttribute("aria-label", "导出文本");
  exportMarkdownButton.title = "导出文本";
  exportMarkdownButton.textContent = "⇩";

  const exportJsonButton = document.createElement("button");
  exportJsonButton.type = "button";
  exportJsonButton.className = "conversation-action conversation-export-json";
  exportJsonButton.dataset.action = "export-json";
  exportJsonButton.dataset.id = conversation.id;
  exportJsonButton.setAttribute("aria-label", "导出数据");
  exportJsonButton.title = "导出数据";
  exportJsonButton.textContent = "{}";

  const deleteButton = document.createElement("button");
  deleteButton.type = "button";
  deleteButton.className = "conversation-action conversation-delete";
  deleteButton.dataset.action = "delete";
  deleteButton.dataset.id = conversation.id;
  deleteButton.setAttribute("aria-label", "删除会话");
  deleteButton.title = "删除会话";
  deleteButton.textContent = "×";

  row.append(title, meta, renameButton, exportMarkdownButton, exportJsonButton, deleteButton);
  return row;
}

async function handleConversationClick(event) {
  const actionTarget = event.target.closest("[data-action]");
  if (!actionTarget || !elements.conversationList.contains(actionTarget)) return;

  const conversationId = actionTarget.dataset.id;
  if (!conversationId || state.streaming) return;

  if (actionTarget.dataset.action === "delete") {
    event.preventDefault();
    event.stopPropagation();
    await deleteConversation(conversationId);
    return;
  }

  if (actionTarget.dataset.action === "rename") {
    event.preventDefault();
    event.stopPropagation();
    await renameConversation(conversationId);
    return;
  }

  if (actionTarget.dataset.action === "export-markdown") {
    event.preventDefault();
    event.stopPropagation();
    await downloadConversation(conversationId, "markdown");
    return;
  }

  if (actionTarget.dataset.action === "export-json") {
    event.preventDefault();
    event.stopPropagation();
    await downloadConversation(conversationId, "json");
    return;
  }

  await loadConversation(conversationId);
}

function handleConversationKeydown(event) {
  if (event.key !== "Enter" && event.key !== " ") return;

  const row = event.target.closest(".conversation-item[data-action='load']");
  if (!row || event.target.closest(".conversation-action")) return;

  event.preventDefault();
  void loadConversation(row.dataset.id);
}

async function loadConversation(conversationId) {
  const response = await apiFetch(`/api/conversations/${conversationId}`);
  const conversation = await readJson(response);
  if (!response.ok || !conversation) {
    setConnection("error", conversation?.error ?? "会话加载失败");
    return;
  }

  state.conversationId = conversation.id;
  state.loadedConversationMessages = conversation.messages ?? [];
  state.visibleMessageStart = Math.max(0, state.loadedConversationMessages.length - state.messagePageSize);
  applyConversationSettings(conversation);
  renderLoadedConversationMessages();

  renderConversations();
  setConnection("ready", "会话已加载");
  collapseSidebarOnMobile();
  elements.messageInput.focus();
}

function handleMessagesClick(event) {
  const actionTarget = event.target.closest("[data-action]");
  if (actionTarget?.dataset.action !== "load-older-messages") {
    return;
  }

  const previousScrollHeight = elements.messages.scrollHeight;
  state.visibleMessageStart = Math.max(0, state.visibleMessageStart - state.messagePageSize);
  renderLoadedConversationMessages({ preserveTop: true, previousScrollHeight });
}

function renderLoadedConversationMessages(options = {}) {
  const fragment = document.createDocumentFragment();

  if (state.visibleMessageStart > 0) {
    const loadOlder = document.createElement("button");
    loadOlder.type = "button";
    loadOlder.className = "ghost load-older-messages";
    loadOlder.dataset.action = "load-older-messages";
    loadOlder.textContent = "加载更早消息";
    fragment.append(loadOlder);
  }

  for (const message of state.loadedConversationMessages.slice(state.visibleMessageStart)) {
    fragment.append(createMessageNode(
      message.role === "user" ? "user" : "assistant",
      message.content,
      {
        attachments: message.attachments ?? [],
        thinkingContent: message.thinkingContent
      }));
  }

  elements.messages.replaceChildren(fragment);

  if (options.preserveTop) {
    elements.messages.scrollTop = elements.messages.scrollHeight - options.previousScrollHeight;
  } else {
    elements.messages.scrollTop = elements.messages.scrollHeight;
  }
}

function applyConversationSettings(conversation) {
  setSelectValue(elements.providerSelect, conversation.providerId);
  renderProviderCatalog();
  setSelectValue(elements.modelSelect, conversation.modelName);
  renderThinkingSettings();
  setSelectValue(elements.presetSelect, conversation.presetId);
  elements.customPrompt.value = conversation.customPrompt ?? "";
  renderCustomPromptState();
}

async function renameConversation(conversationId) {
  const current = state.conversations.find(item => item.id === conversationId);
  const title = window.prompt("新的会话标题", current?.title ?? "");
  if (title == null) return;

  const trimmed = title.trim();
  if (!trimmed) {
    setConnection("error", "标题不能为空");
    return;
  }

  setConnection("ready", "正在重命名会话");
  const response = await apiFetch(`/api/conversations/${conversationId}/title`, {
    method: "PATCH",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ title: trimmed })
  });

  if (!response.ok) {
    setConnection("error", "重命名失败");
    return;
  }

  updateConversationInList(conversationId, {
    title: trimmed,
    updatedAt: new Date().toISOString()
  });
  setConnection("ready", "会话已重命名");
}

async function downloadConversation(conversationId, format) {
  setConnection("ready", format === "json" ? "正在导出数据" : "正在导出文本");
  const response = await apiFetch(`/api/export/${conversationId}/${format}`);
  if (!response.ok) {
    setConnection("error", "导出失败");
    return;
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = getDownloadFileName(response) ?? `conversation.${format === "json" ? "json" : "md"}`;
  link.rel = "noreferrer";
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

async function deleteConversation(conversationId) {
  setConnection("ready", "正在删除会话");
  const response = await apiFetch(`/api/conversations/${conversationId}`, { method: "DELETE" });
  if (!response.ok) {
    setConnection("error", "删除失败");
    return;
  }

  removeConversationFromList(conversationId);
  if (state.conversationId === conversationId) {
    startNewChat();
  }

  setConnection("ready", "会话已删除");
}

function updateConversationInList(conversationId, updates) {
  state.conversations = state.conversations.map(conversation =>
    conversation.id === conversationId ? { ...conversation, ...updates } : conversation);
  renderConversations();
}

function removeConversationFromList(conversationId) {
  state.conversations = state.conversations.filter(conversation => conversation.id !== conversationId);
  state.conversationOffset = state.conversations.length;
  renderConversations();
}

async function sendMessage(event) {
  event.preventDefault();
  const message = elements.messageInput.value.trim();
  const images = state.pendingImages;
  if ((!message && images.length === 0) || state.streaming) return;

  if (!state.user) {
    setAuthMessage("请先登录");
    setConnection("error", "需要登录");
    return;
  }

  state.streaming = true;
  elements.sendButton.disabled = true;
  elements.messageInput.value = "";
  state.pendingImages = [];
  renderAttachmentPreview();
  resizeComposer();

  appendMessage("user", message, { attachments: images });
  const assistant = appendMessage("assistant", "");
  const assistantRenderer = createAssistantRenderer(assistant);
  setConnection("ready", "正在生成");

  try {
    const response = await apiFetch("/api/chat/stream", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        conversationId: state.conversationId,
        message,
        images,
        enableSearch: elements.enableSearch.checked,
        providerId: elements.providerSelect.value || null,
        modelName: elements.modelSelect.value || null,
        presetId: elements.presetSelect.value || null,
        customPrompt: elements.presetSelect.value === "custom" ? elements.customPrompt.value : null,
        ...getThinkingPayload()
      })
    });

    if (!response.ok || !response.body) {
      const error = await readJson(response);
      throw new Error(error?.error ?? `请求失败：${response.status}`);
    }

    await readEventStream(response.body, {
      update: payload => assistantRenderer.update(payload),
      final: payload => {
        state.conversationId = payload.conversationId ?? state.conversationId;
        assistantRenderer.flush({
          content: payload.succeeded ? payload.content : payload.errorMessage ?? payload.content,
          thinkingContent: payload.thinkingContent,
          citations: payload.citations
        });
      }
    });

    await refreshConversations();
    setConnection("ready", "API 已连接");
  } catch (error) {
    assistantRenderer.flush({ content: error.message });
    setConnection("error", "请求失败");
  } finally {
    state.streaming = false;
    elements.sendButton.disabled = false;
    elements.messageInput.focus();
  }
}

function createAssistantRenderer(node) {
  let latestPayload = null;
  let timerId = null;

  const renderLatest = () => {
    timerId = null;
    if (!latestPayload) return;
    updateAssistant(node, latestPayload);
  };

  return {
    update(payload) {
      latestPayload = payload;
      if (timerId == null) {
        timerId = window.setTimeout(renderLatest, STREAM_RENDER_INTERVAL_MS);
      }
    },
    flush(payload) {
      latestPayload = payload ?? latestPayload;
      if (timerId != null) {
        window.clearTimeout(timerId);
        timerId = null;
      }
      renderLatest();
    }
  };
}

function appendMessage(role, content, options = {}) {
  const node = createMessageNode(role, content, options);
  elements.messages.append(node);
  elements.messages.scrollTop = elements.messages.scrollHeight;
  return node;
}

function createMessageNode(role, content, options = {}) {
  const node = elements.template.content.firstElementChild.cloneNode(true);
  node.classList.add(role);
  node.querySelector(".message-role").textContent = role === "user" ? "YOU" : role === "assistant" ? "ASSISTANT" : "SYSTEM";
  renderMessageContent(node.querySelector(".message-content"), content, role);
  renderMessageAttachments(node, options.attachments ?? []);

  if (options.thinkingContent) {
    const thinking = node.querySelector(".thinking");
    thinking.classList.remove("hidden");
    thinking.querySelector("pre").textContent = options.thinkingContent;
  }

  return node;
}

function renderMessageAttachments(node, attachments) {
  const container = node.querySelector(".message-attachments");
  container.replaceChildren();
  container.classList.toggle("hidden", attachments.length === 0);

  for (const image of attachments) {
    const img = document.createElement("img");
    img.src = toDataUrl(image);
    img.alt = image.fileName || "已附加图片";
    container.append(img);
  }
}

function updateAssistant(node, payload) {
  renderMessageContent(node.querySelector(".message-content"), payload.content ?? "", "assistant");

  const thinking = node.querySelector(".thinking");
  if (payload.thinkingContent) {
    thinking.classList.remove("hidden");
    thinking.querySelector("pre").textContent = payload.thinkingContent;
  }

  const citations = node.querySelector(".citations");
  citations.replaceChildren();
  if (payload.citations?.length) {
    citations.classList.remove("hidden");
    for (const citation of payload.citations) {
      const link = document.createElement("a");
      link.href = citation.uri;
      link.target = "_blank";
      link.rel = "noreferrer";
      link.textContent = citation.title || citation.uri;
      citations.append(link);
    }
  }

  elements.messages.scrollTop = elements.messages.scrollHeight;
}

function renderMessageContent(container, content, role) {
  container.replaceChildren();
  if (!content) return;

  if (role === "assistant") {
    container.append(...parseMarkdown(content));
    return;
  }

  container.textContent = content;
}

function parseMarkdown(markdown) {
  const lines = markdown.replace(/\r\n?/g, "\n").split("\n");
  const nodes = [];
  let paragraph = [];
  let list = null;
  let codeFence = null;

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    const p = document.createElement("p");
    renderInlineMarkdown(p, paragraph.join("\n"));
    nodes.push(p);
    paragraph = [];
  };

  const flushList = () => {
    if (!list) return;
    nodes.push(list.element);
    list = null;
  };

  const flushCodeFence = () => {
    if (!codeFence) return;
    const pre = document.createElement("pre");
    const code = document.createElement("code");
    if (codeFence.language) {
      code.dataset.language = codeFence.language;
    }
    code.textContent = codeFence.lines.join("\n");
    pre.append(code);
    nodes.push(pre);
    codeFence = null;
  };

  for (const line of lines) {
    const fenceMatch = line.match(/^```([\w.+-]*)\s*$/);
    if (fenceMatch) {
      if (codeFence) {
        flushCodeFence();
      } else {
        flushParagraph();
        flushList();
        codeFence = { language: fenceMatch[1], lines: [] };
      }
      continue;
    }

    if (codeFence) {
      codeFence.lines.push(line);
      continue;
    }

    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }

    const headingMatch = line.match(/^(#{1,6})\s+(.+)$/);
    if (headingMatch) {
      flushParagraph();
      flushList();
      const heading = document.createElement(`h${headingMatch[1].length}`);
      renderInlineMarkdown(heading, headingMatch[2].trim());
      nodes.push(heading);
      continue;
    }

    const quoteMatch = line.match(/^>\s?(.+)$/);
    if (quoteMatch) {
      flushParagraph();
      flushList();
      const blockquote = document.createElement("blockquote");
      renderInlineMarkdown(blockquote, quoteMatch[1]);
      nodes.push(blockquote);
      continue;
    }

    const listMatch = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.+)$/);
    if (listMatch) {
      flushParagraph();
      const ordered = /\d/.test(listMatch[2][0]);
      if (!list || list.ordered !== ordered) {
        flushList();
        list = {
          ordered,
          element: document.createElement(ordered ? "ol" : "ul")
        };
      }
      const item = document.createElement("li");
      renderInlineMarkdown(item, listMatch[3]);
      list.element.append(item);
      continue;
    }

    paragraph.push(line);
  }

  flushCodeFence();
  flushParagraph();
  flushList();

  if (nodes.length === 0) {
    return [document.createTextNode(markdown)];
  }

  return nodes;
}

function renderInlineMarkdown(container, text) {
  const tokenPattern = /(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\*[^*\n]+\*|_[^_\n]+_|\[([^\]\n]+)\]\(([^)\s]+)\))/g;
  let cursor = 0;
  let match;

  while ((match = tokenPattern.exec(text)) !== null) {
    if (match.index > cursor) {
      appendTextWithBreaks(container, text.slice(cursor, match.index));
    }

    const token = match[0];
    if (token.startsWith("`")) {
      const code = document.createElement("code");
      code.textContent = token.slice(1, -1);
      container.append(code);
    } else if (token.startsWith("**") || token.startsWith("__")) {
      const strong = document.createElement("strong");
      renderInlineMarkdown(strong, token.slice(2, -2));
      container.append(strong);
    } else if (token.startsWith("*") || token.startsWith("_")) {
      const em = document.createElement("em");
      renderInlineMarkdown(em, token.slice(1, -1));
      container.append(em);
    } else {
      const link = createSafeLink(match[2], match[3]);
      container.append(link ?? document.createTextNode(token));
    }

    cursor = tokenPattern.lastIndex;
  }

  if (cursor < text.length) {
    appendTextWithBreaks(container, text.slice(cursor));
  }
}

function appendTextWithBreaks(container, text) {
  const parts = text.split("\n");
  parts.forEach((part, index) => {
    if (index > 0) {
      container.append(document.createElement("br"));
    }
    if (part) {
      container.append(document.createTextNode(part));
    }
  });
}

function createSafeLink(label, href) {
  try {
    const url = new URL(href, window.location.href);
    if (!["http:", "https:", "mailto:", "tel:"].includes(url.protocol)) {
      return null;
    }

    const link = document.createElement("a");
    link.href = url.href;
    link.target = "_blank";
    link.rel = "noreferrer";
    link.textContent = label;
    return link;
  } catch {
    return null;
  }
}

async function readEventStream(body, handlers) {
  const reader = body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += value;
    let boundary = buffer.indexOf("\n\n");
    while (boundary >= 0) {
      const rawEvent = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      dispatchEventBlock(rawEvent, handlers);
      boundary = buffer.indexOf("\n\n");
    }
  }
}

function dispatchEventBlock(rawEvent, handlers) {
  const lines = rawEvent.split("\n");
  const eventName = lines.find(line => line.startsWith("event:"))?.slice(6).trim();
  const data = lines
    .filter(line => line.startsWith("data:"))
    .map(line => line.slice(5).trimStart())
    .join("\n");

  if (!eventName || !data || !handlers[eventName]) {
    return;
  }

  handlers[eventName](JSON.parse(data));
}

async function apiFetch(input, init = {}) {
  const headers = new Headers(init.headers ?? {});
  const token = await getAuthToken();
  if (token) {
    headers.set("authorization", `Bearer ${token}`);
  }

  return fetch(input, {
    ...init,
    headers
  });
}

async function getAuthToken() {
  if (!state.firebaseAuth?.currentUser) {
    return null;
  }

  return state.firebaseAuth.currentUser.getIdToken();
}

async function readJson(response) {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function toAuthErrorMessage(error) {
  const code = error?.code ?? "";
  if (code.includes("invalid-email")) return "邮箱格式不正确";
  if (code.includes("user-not-found") || code.includes("wrong-password") || code.includes("invalid-credential")) {
    return "邮箱或密码错误";
  }
  if (code.includes("email-already-in-use")) return "该邮箱已被注册";
  if (code.includes("weak-password")) return "密码强度不足";
  if (code.includes("network-request-failed")) return "网络连接失败";
  return "认证失败";
}

function getDownloadFileName(response) {
  const disposition = response.headers.get("content-disposition");
  const match = disposition?.match(/filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i);
  const encoded = match?.[1];
  if (encoded) {
    try {
      return decodeURIComponent(encoded);
    } catch {
      return encoded;
    }
  }

  return match?.[2] ?? null;
}

function setAuthMessage(message) {
  elements.authMessage.textContent = message;
}

function setSettingsMessage(message) {
  elements.settingsMessage.textContent = message;
}

function setConnection(kind, message) {
  elements.connectionState.classList.toggle("ready", kind === "ready");
  elements.connectionState.classList.toggle("error", kind === "error");
  elements.connectionState.textContent = message;
}

function setSelectValue(select, value) {
  if (!value) return false;

  const exists = Array.from(select.options).some(option => option.value === value);
  if (exists) {
    select.value = value;
    return true;
  }
  return false;
}

function resizeComposer() {
  elements.messageInput.style.height = "auto";
  elements.messageInput.style.height = `${Math.min(elements.messageInput.scrollHeight, 180)}px`;
}

async function handleImageSelection(event) {
  const files = Array.from(event.target.files ?? []);
  if (files.length === 0) return;

  const remainingSlots = Math.max(0, 5 - state.pendingImages.length);
  const selected = files.slice(0, remainingSlots);

  for (const file of selected) {
    if (!file.type.startsWith("image/")) continue;
    if (file.size > MAX_IMAGE_BYTES) {
      setConnection("error", "单张图片最大支持 4MB");
      continue;
    }

    state.pendingImages.push({
      base64Data: await readFileAsBase64(file),
      mimeType: file.type,
      fileName: file.name
    });
  }

  elements.imageInput.value = "";
  renderAttachmentPreview();
}

function renderAttachmentPreview() {
  elements.attachmentPreview.replaceChildren();
  elements.attachmentPreview.classList.toggle("hidden", state.pendingImages.length === 0);

  state.pendingImages.forEach((image, index) => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "attachment-chip";
    item.dataset.index = String(index);

    const thumb = document.createElement("img");
    thumb.src = toDataUrl(image);
    thumb.alt = image.fileName || "pending image";

    const name = document.createElement("span");
    name.textContent = image.fileName || "image";

    const remove = document.createElement("strong");
    remove.textContent = "×";

    item.append(thumb, name, remove);
    elements.attachmentPreview.append(item);
  });
}

function removePendingImage(event) {
  const item = event.target.closest("[data-index]");
  if (!item) return;

  state.pendingImages.splice(Number.parseInt(item.dataset.index, 10), 1);
  renderAttachmentPreview();
}

function clearPendingImages() {
  state.pendingImages = [];
  elements.imageInput.value = "";
  renderAttachmentPreview();
}

function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      const result = String(reader.result ?? "");
      resolve(result.includes(",") ? result.split(",", 2)[1] : result);
    });
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

function toDataUrl(image) {
  return `data:${image.mimeType};base64,${image.base64Data}`;
}

function formatConversationMeta(conversation) {
  const updated = new Date(conversation.updatedAt);
  const date = Number.isNaN(updated.valueOf())
    ? ""
    : updated.toLocaleDateString("zh-CN", { month: "numeric", day: "numeric" });
  const provider = conversation.providerId ? `${conversation.providerId}:${conversation.modelName || "model"}` : conversation.presetId;
  const tokens = conversation.tokenCount > 0 ? `${conversation.tokenCount} tokens` : "";
  return [date, provider, tokens].filter(Boolean).join(" · ");
}
