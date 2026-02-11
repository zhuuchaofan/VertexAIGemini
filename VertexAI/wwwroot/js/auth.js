// Gemini Chat - Auth Helper
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
