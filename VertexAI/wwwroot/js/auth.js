// Gemini Chat - Auth Helper
// 用于在浏览器端发起认证请求，以正确处理 HttpOnly Cookie

window.authFetch = async function (endpoint, email, password) {
  try {
    const response = await fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ email, password }),
      credentials: "same-origin", // 确保 Cookie 被包含
    });

    const data = await response.json();
    return {
      success: data.success === true,
      error: data.error || null,
    };
  } catch (error) {
    return {
      success: false,
      error: error.message || "网络错误",
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
