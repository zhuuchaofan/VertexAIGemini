/** Firebase's error codes in plain language. An unknown code is shown as it is, so nothing is hidden behind a vague message. */
export function authErrorMessage(error: unknown): string {
  const code = typeof error === "object" && error !== null && "code" in error ? String((error as { code: unknown }).code) : "";
  if (code.includes("invalid-email")) return "邮箱格式不正确";
  if (code.includes("user-not-found") || code.includes("wrong-password") || code.includes("invalid-credential")) return "邮箱或密码错误";
  if (code.includes("email-already-in-use")) return "该邮箱已被注册";
  if (code.includes("weak-password")) return "密码强度不足";
  if (code.includes("operation-not-allowed")) return "该登录方式尚未在 Firebase 启用";
  if (code.includes("unauthorized-domain")) return "当前域名未加入 Firebase 授权域名";
  if (code.includes("app-not-authorized") || code.includes("api-key-not-valid")) return "Firebase API Key 未授权当前域名";
  if (code.includes("popup-blocked")) return "浏览器阻止了登录窗口，请允许弹窗后重试";
  if (code.includes("popup-closed-by-user") || code.includes("cancelled-popup-request")) return "登录窗口已关闭，请重试";
  if (code.includes("account-exists-with-different-credential")) return "该邮箱已绑定其他登录方式";
  if (code.includes("too-many-requests")) return "尝试次数过多，请稍后再试";
  if (code.includes("network-request-failed")) return "网络连接失败";
  return code ? `认证失败：${code}` : "认证失败";
}
