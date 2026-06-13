// Antigravity Studio - Auth Helper
// 用于在浏览器端发起认证请求，以正确处理 HttpOnly Cookie

window.authFetch = async function (endpoint, email, password) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);

  try {
    const response = await fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ email, password }),
      credentials: "same-origin",
      signal: controller.signal,
    });

    clearTimeout(timeout);

    const data = await response.json();
    return {
      success: data.success === true,
      error: data.error || null,
    };
  } catch (error) {
    clearTimeout(timeout);

    if (error.name === "AbortError") {
      return {
        success: false,
        error: "请求超时，请检查网络后重试",
      };
    }

    return {
      success: false,
      error: "网络连接失败，请稍后重试",
    };
  }
};

// 用于登出
window.authLogout = async function () {
  try {
    await fetch("/api/auth/logout", {
      method: "POST",
      credentials: "same-origin",
    });
    return true;
  } catch {
    return false;
  }
};

// 通用 POST 请求（用于密码重置等非登录表单）
window.simpleFetch = async function (endpoint, body) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);

  try {
    const response = await fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
      credentials: "same-origin",
      signal: controller.signal,
    });

    clearTimeout(timeout);

    const data = await response.json();
    return {
      success: data.success === true,
      error: data.error || null,
    };
  } catch (error) {
    clearTimeout(timeout);

    if (error.name === "AbortError") {
      return {
        success: false,
        error: "请求超时，请检查网络后重试",
      };
    }

    return {
      success: false,
      error: "网络连接失败，请稍后重试",
    };
  }
};

// 文件下载辅助函数
window.downloadFile = (url, fileName) => {
  const anchor = document.createElement("a");
  anchor.href = url;
  if (fileName) {
    anchor.download = fileName;
  }
  anchor.target = "_blank";
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
};

// 下拉菜单辅助控制 - 避免 Blazor Server 的 WebSocket 往返延迟导致下拉菜单慢半拍
window.dropdownHelper = {
  toggle: function (menuId, event) {
    if (event) {
      event.stopPropagation();
    }
    const menus = ['model-menu', 'preset-menu', 'thinking-menu', 'export-menu'];
    menus.forEach(id => {
      const el = document.getElementById(id);
      if (el) {
        if (id === menuId) {
          el.classList.toggle('hidden');
        } else {
          el.classList.add('hidden');
        }
      }
    });
  },
  selectModel: function (button) {
    if (!button || !button.dataset) return;

    const modelName = button.dataset.modelName;
    const modelLabel = button.dataset.modelLabel || modelName;
    if (!modelName) return;

    const label = document.getElementById('current-model-label');
    if (label) {
      label.textContent = modelLabel;
    }

    document.querySelectorAll('[data-model-option]').forEach(option => {
      const isSelected = option.dataset.modelName === modelName;
      option.classList.toggle('bg-slate-50', isSelected);

      const dot = option.querySelector('[data-model-dot]');
      if (dot) {
        dot.classList.toggle('bg-emerald-500', isSelected);
        dot.classList.toggle('bg-slate-300', !isSelected);
      }
    });

    try {
      localStorage.setItem('geminiModel', modelName);
    } catch {
      // localStorage may be unavailable in private or restricted contexts.
    }
  },
  initGlobalClose: function () {
    if (window.dropdownHelper._hasInit) return;
    window.dropdownHelper._hasInit = true;
    document.addEventListener('click', function () {
      const menus = ['model-menu', 'preset-menu', 'thinking-menu', 'export-menu'];
      menus.forEach(id => {
        const el = document.getElementById(id);
        if (el) {
          el.classList.add('hidden');
        }
      });
    });
  }
};
