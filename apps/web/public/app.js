const mobileQuery = window.matchMedia("(max-width: 820px)");
const savedSidebarState = localStorage.getItem("vertex-sidebar-collapsed");

const state = {
  mode: "login",
  user: null,
  conversationId: null,
  conversations: [],
  workspaceConfig: null,
  pendingImages: [],
  streaming: false,
  sidebarCollapsed: mobileQuery.matches ? true : savedSidebarState === "true"
};

const MAX_IMAGE_BYTES = 4 * 1024 * 1024;

const elements = {
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
  sessionLabel: document.querySelector("#session-label"),
  connectionState: document.querySelector("#connection-state"),
  providerSelect: document.querySelector("#provider-select"),
  modelSelect: document.querySelector("#model-select"),
  presetSelect: document.querySelector("#preset-select"),
  customPromptLabel: document.querySelector("#custom-prompt-label"),
  customPrompt: document.querySelector("#custom-prompt"),
  enableSearch: document.querySelector("#enable-search"),
  newChat: document.querySelector("#new-chat"),
  refreshConversations: document.querySelector("#refresh-conversations"),
  conversationList: document.querySelector("#conversation-list"),
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
elements.newChat.addEventListener("click", () => {
  startNewChat();
  collapseSidebarOnMobile();
});
elements.refreshConversations.addEventListener("click", refreshConversations);
elements.conversationList.addEventListener("click", handleConversationClick);
elements.providerSelect.addEventListener("change", renderProviderCatalog);
elements.presetSelect.addEventListener("change", renderCustomPromptState);
elements.chatForm.addEventListener("submit", sendMessage);
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
await Promise.all([loadWorkspaceConfig(), refreshSession()]);
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

async function submitAuth(event) {
  event.preventDefault();
  const form = new FormData(elements.authForm);
  const payload = {
    email: String(form.get("email") ?? ""),
    password: String(form.get("password") ?? "")
  };

  setAuthMessage("正在处理...");

  const response = await fetch(`/api/auth/${state.mode}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload)
  });
  const result = await readJson(response);

  if (!response.ok || !result?.success) {
    setAuthMessage(result?.error ?? "认证失败");
    setConnection("error", "认证失败");
    return;
  }

  state.user = result.user;
  renderSession();
  await refreshConversations();
  setAuthMessage(state.mode === "login" ? "已登录" : "已注册并登录");
  setConnection("ready", "API 已连接");
}

async function logout() {
  await fetch("/api/auth/logout", { method: "POST" });
  state.user = null;
  state.conversationId = null;
  state.conversations = [];
  renderSession();
  renderConversations();
  startNewChat();
  setAuthMessage("已退出登录");
  setConnection("", "API 未连接");
}

async function refreshSession() {
  const response = await fetch("/api/auth/status");
  const result = await readJson(response);
  if (response.ok && result?.success) {
    state.user = result.user;
    setConnection("ready", "API 已连接");
    await refreshConversations();
  }

  renderSession();
}

function renderSession() {
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

function startNewChat() {
  state.conversationId = null;
  clearPendingImages();
  renderConversations();
  elements.messages.replaceChildren();
  appendMessage("system", "新的球球布丁工作室会话已就绪。登录后发送消息会通过 /api/chat/stream 调用后端模型。");
  if (state.user) {
    elements.messageInput.focus();
  }
}

async function loadWorkspaceConfig() {
  const response = await fetch("/api/workspace/config");
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

  if (state.workspaceConfig?.defaultProviderId) {
    elements.providerSelect.value = state.workspaceConfig.defaultProviderId;
  }

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

  setSelectValue(elements.modelSelect, providerConfig?.defaultModelName);
  setSelectValue(elements.presetSelect, providerConfig?.defaultPresetId);

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

async function refreshConversations() {
  if (!state.user) {
    state.conversations = [];
    renderConversations();
    return;
  }

  const response = await fetch("/api/conversations/");
  if (!response.ok) {
    elements.conversationEmpty.textContent = "无法加载会话";
    return;
  }

  state.conversations = await response.json();
  renderConversations();
}

function renderConversations() {
  elements.conversationList.replaceChildren();

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
}

function createConversationItem(conversation) {
  const row = document.createElement("button");
  row.type = "button";
  row.className = "conversation-item";
  row.dataset.action = "load";
  row.dataset.id = conversation.id;
  row.classList.toggle("active", conversation.id === state.conversationId);

  const title = document.createElement("span");
  title.className = "conversation-title";
  title.textContent = conversation.title || "未命名对话";

  const meta = document.createElement("span");
  meta.className = "conversation-meta";
  meta.textContent = formatConversationMeta(conversation);

  const renameButton = document.createElement("span");
  renameButton.className = "conversation-action conversation-rename";
  renameButton.dataset.action = "rename";
  renameButton.dataset.id = conversation.id;
  renameButton.title = "重命名会话";
  renameButton.textContent = "✎";

  const exportMarkdownButton = document.createElement("span");
  exportMarkdownButton.className = "conversation-action conversation-export-md";
  exportMarkdownButton.dataset.action = "export-markdown";
  exportMarkdownButton.dataset.id = conversation.id;
  exportMarkdownButton.title = "导出文本";
  exportMarkdownButton.textContent = "⇩";

  const exportJsonButton = document.createElement("span");
  exportJsonButton.className = "conversation-action conversation-export-json";
  exportJsonButton.dataset.action = "export-json";
  exportJsonButton.dataset.id = conversation.id;
  exportJsonButton.title = "导出数据";
  exportJsonButton.textContent = "{}";

  const deleteButton = document.createElement("span");
  deleteButton.className = "conversation-action conversation-delete";
  deleteButton.dataset.action = "delete";
  deleteButton.dataset.id = conversation.id;
  deleteButton.title = "删除会话";
  deleteButton.textContent = "×";

  row.append(title, meta, renameButton, exportMarkdownButton, exportJsonButton, deleteButton);
  return row;
}

async function handleConversationClick(event) {
  const actionTarget = event.target.closest("[data-action]");
  if (!actionTarget) return;

  const conversationId = actionTarget.dataset.id;
  if (!conversationId || state.streaming) return;

  if (actionTarget.dataset.action === "delete") {
    event.stopPropagation();
    await deleteConversation(conversationId);
    return;
  }

  if (actionTarget.dataset.action === "rename") {
    event.stopPropagation();
    await renameConversation(conversationId);
    return;
  }

  if (actionTarget.dataset.action === "export-markdown") {
    event.stopPropagation();
    downloadConversation(conversationId, "markdown");
    return;
  }

  if (actionTarget.dataset.action === "export-json") {
    event.stopPropagation();
    downloadConversation(conversationId, "json");
    return;
  }

  await loadConversation(conversationId);
}

async function loadConversation(conversationId) {
  const response = await fetch(`/api/conversations/${conversationId}`);
  const conversation = await readJson(response);
  if (!response.ok || !conversation) {
    setConnection("error", conversation?.error ?? "会话加载失败");
    return;
  }

  state.conversationId = conversation.id;
  applyConversationSettings(conversation);
  elements.messages.replaceChildren();
  for (const message of conversation.messages) {
    const node = appendMessage(message.role === "user" ? "user" : "assistant", message.content, {
      attachments: message.attachments ?? []
    });
    if (message.thinkingContent) {
      updateAssistant(node, {
        content: message.content,
        thinkingContent: message.thinkingContent
      });
    }
  }

  renderConversations();
  setConnection("ready", "会话已加载");
  collapseSidebarOnMobile();
  elements.messageInput.focus();
}

function applyConversationSettings(conversation) {
  setSelectValue(elements.providerSelect, conversation.providerId);
  renderProviderCatalog();
  setSelectValue(elements.modelSelect, conversation.modelName);
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

  const response = await fetch(`/api/conversations/${conversationId}/title`, {
    method: "PATCH",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ title: trimmed })
  });

  if (!response.ok) {
    setConnection("error", "重命名失败");
    return;
  }

  await refreshConversations();
  setConnection("ready", "会话已重命名");
}

function downloadConversation(conversationId, format) {
  const link = document.createElement("a");
  link.href = `/api/export/${conversationId}/${format}`;
  link.download = "";
  link.rel = "noreferrer";
  document.body.append(link);
  link.click();
  link.remove();
  setConnection("ready", format === "json" ? "正在导出数据" : "正在导出文本");
}

async function deleteConversation(conversationId) {
  const response = await fetch(`/api/conversations/${conversationId}`, { method: "DELETE" });
  if (!response.ok) {
    setConnection("error", "删除失败");
    return;
  }

  if (state.conversationId === conversationId) {
    startNewChat();
  }

  await refreshConversations();
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
  setConnection("ready", "正在生成");

  try {
    const response = await fetch("/api/chat/stream", {
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
        customPrompt: elements.presetSelect.value === "custom" ? elements.customPrompt.value : null
      })
    });

    if (!response.ok || !response.body) {
      const error = await readJson(response);
      throw new Error(error?.error ?? `请求失败：${response.status}`);
    }

    await readEventStream(response.body, {
      update: payload => updateAssistant(assistant, payload),
      final: payload => {
        state.conversationId = payload.conversationId ?? state.conversationId;
        updateAssistant(assistant, {
          content: payload.succeeded ? payload.content : payload.errorMessage ?? payload.content,
          thinkingContent: payload.thinkingContent,
          citations: payload.citations
        });
      }
    });

    await refreshConversations();
    setConnection("ready", "API 已连接");
  } catch (error) {
    updateAssistant(assistant, { content: error.message });
    setConnection("error", "请求失败");
  } finally {
    state.streaming = false;
    elements.sendButton.disabled = false;
    elements.messageInput.focus();
  }
}

function appendMessage(role, content, options = {}) {
  const node = elements.template.content.firstElementChild.cloneNode(true);
  node.classList.add(role);
  node.querySelector(".message-role").textContent = role === "user" ? "YOU" : role === "assistant" ? "ASSISTANT" : "SYSTEM";
  node.querySelector(".message-content").textContent = content;
  renderMessageAttachments(node, options.attachments ?? []);
  elements.messages.append(node);
  elements.messages.scrollTop = elements.messages.scrollHeight;
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
  node.querySelector(".message-content").textContent = payload.content ?? "";

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

async function readJson(response) {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function setAuthMessage(message) {
  elements.authMessage.textContent = message;
}

function setConnection(kind, message) {
  elements.connectionState.classList.toggle("ready", kind === "ready");
  elements.connectionState.classList.toggle("error", kind === "error");
  elements.connectionState.textContent = message;
}

function setSelectValue(select, value) {
  if (!value) return;

  const exists = Array.from(select.options).some(option => option.value === value);
  if (exists) {
    select.value = value;
  }
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
