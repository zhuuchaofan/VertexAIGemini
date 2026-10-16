// Per-model "thinking" controls (level select or budget toggle) rendered into a container.
// Preference storage and model lookup are injected so this module stays DOM-local.
export function createThinkingSettings(container, deps) {
  function saveThinkingPreference() {
    const thinking = deps.getSelectedModel()?.thinking;
    if (!thinking) return;

    let value;
    if (thinking.kind === "qwen-budget") {
      value = {
        enabled: container.querySelector("#thinking-enabled")?.checked ?? true,
        budget: container.querySelector("#thinking-budget-select")?.value ?? "custom",
        customBudget: container.querySelector("#thinking-budget-custom")?.value ?? ""
      };
    } else {
      value = {
        level: container.querySelector("#thinking-level")?.value ?? thinking.default ?? "on"
      };
    }

    deps.writePreference(value);
  }

  function renderThinkingSettings() {
    const thinking = deps.getSelectedModel()?.thinking;
    container.replaceChildren();

    if (!thinking) {
      container.classList.add("hidden");
      return;
    }

    container.classList.remove("hidden");

    const heading = document.createElement("div");
    heading.className = "thinking-heading";
    heading.textContent = "思考设置";
    container.append(heading);

    const hasChoices = (thinking.options?.length ?? 0) > 0 || (thinking.budgets?.length ?? 0) > 0;
    if (thinking.fixedEnabled && !hasChoices) {
      const readonly = document.createElement("div");
      readonly.className = "thinking-readonly";
      readonly.textContent = "始终开启";
      container.append(readonly);
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

    const saved = deps.readPreference();
    deps.setSelectValue(select, saved?.level ?? thinking.default ?? options[0]?.value ?? "on");
    select.addEventListener("change", saveThinkingPreference);
    label.append(select);
    container.append(label);
  }

  function renderBudgetThinking(thinking) {
    const enabledLabel = document.createElement("label");
    enabledLabel.className = "toggle-row";

    const enabled = document.createElement("input");
    enabled.id = "thinking-enabled";
    enabled.type = "checkbox";
    const saved = deps.readPreference();
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
    container.append(enabledLabel, row);
    updateBudgetState();
  }

  function getThinkingPayload() {
    const thinking = deps.getSelectedModel()?.thinking;
    if (!thinking) {
      return {};
    }

    const hasChoices = (thinking.options?.length ?? 0) > 0 || (thinking.budgets?.length ?? 0) > 0;
    if (thinking.fixedEnabled && !hasChoices) {
      return { thinkingEnabled: true };
    }

    if (thinking.kind === "qwen-budget") {
      const enabled = container.querySelector("#thinking-enabled")?.checked ?? true;
      const budgetSelect = container.querySelector("#thinking-budget-select");
      const customBudget = container.querySelector("#thinking-budget-custom");
      const budget = budgetSelect?.value === "custom"
        ? Number(customBudget?.value)
        : Number(budgetSelect?.value);

      return {
        thinkingEnabled: enabled,
        thinkingBudget: enabled && Number.isFinite(budget) && budget > 0 ? Math.floor(budget) : null
      };
    }

    const level = container.querySelector("#thinking-level")?.value ?? thinking.default ?? "on";
    return {
      thinkingEnabled: level !== "off",
      thinkingLevel: level
    };
  }

  return { render: renderThinkingSettings, getPayload: getThinkingPayload };
}
