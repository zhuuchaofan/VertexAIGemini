import { describe, expect, it } from "vitest";
import { authErrorMessage } from "./messages";

describe("authErrorMessage", () => {
  it("names the common failures plainly", () => {
    expect(authErrorMessage({ code: "auth/invalid-credential" })).toBe("邮箱或密码错误");
    expect(authErrorMessage({ code: "auth/wrong-password" })).toBe("邮箱或密码错误");
    expect(authErrorMessage({ code: "auth/email-already-in-use" })).toBe("该邮箱已被注册");
    expect(authErrorMessage({ code: "auth/weak-password" })).toBe("密码强度不足");
    expect(authErrorMessage({ code: "auth/popup-blocked" })).toContain("阻止");
    expect(authErrorMessage({ code: "auth/network-request-failed" })).toBe("网络连接失败");
  });

  it("shows an unknown code as it is, and copes with something that is not a Firebase error", () => {
    expect(authErrorMessage({ code: "auth/some-new-thing" })).toBe("认证失败：auth/some-new-thing");
    expect(authErrorMessage(new Error("boom"))).toBe("认证失败");
    expect(authErrorMessage(null)).toBe("认证失败");
  });
});
