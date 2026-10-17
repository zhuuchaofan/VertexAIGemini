import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LoginForm } from "./LoginForm";

function setup(overrides: Partial<React.ComponentProps<typeof LoginForm>> = {}) {
  const props = {
    error: null,
    onSignIn: vi.fn().mockResolvedValue(undefined),
    onSignUp: vi.fn().mockResolvedValue(undefined),
    onGoogle: vi.fn().mockResolvedValue(undefined),
    onReset: vi.fn().mockResolvedValue(undefined),
    ...overrides
  };
  render(<LoginForm {...props} />);
  return props;
}

const fill = (email: string, password = "secret1") => {
  fireEvent.change(screen.getByLabelText("邮箱"), { target: { value: email } });
  fireEvent.change(screen.getByLabelText("密码"), { target: { value: password } });
};

describe("LoginForm", () => {
  it("signs in with email and password", async () => {
    const props = setup();
    fill(" me@example.com ");
    fireEvent.click(screen.getByRole("button", { name: "登录" }));
    await waitFor(() => expect(props.onSignIn).toHaveBeenCalledWith("me@example.com", "secret1"));
    expect(props.onSignUp).not.toHaveBeenCalled();
  });

  it("registers from its own tab, and the reset link belongs to signing in only", async () => {
    const props = setup();
    expect(screen.getByRole("button", { name: "忘记密码？" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "注册" }));
    expect(screen.queryByRole("button", { name: "忘记密码？" })).toBeNull();
    fill("new@example.com");
    fireEvent.click(screen.getByRole("button", { name: "注册并登录" }));
    await waitFor(() => expect(props.onSignUp).toHaveBeenCalledWith("new@example.com", "secret1"));
    expect(props.onSignIn).not.toHaveBeenCalled();
  });

  it("continues with Google without asking for a password", async () => {
    const props = setup();
    fireEvent.click(screen.getByRole("button", { name: "使用 Google 账号继续" }));
    await waitFor(() => expect(props.onGoogle).toHaveBeenCalled());
  });

  it("asks for the email before sending a reset mail, then says it was sent", async () => {
    const props = setup();
    fireEvent.click(screen.getByRole("button", { name: "忘记密码？" }));
    expect(screen.getByRole("alert")).toHaveTextContent("请输入邮箱");
    expect(props.onReset).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("邮箱"), { target: { value: "me@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "忘记密码？" }));
    await waitFor(() => expect(props.onReset).toHaveBeenCalledWith("me@example.com"));
    expect(await screen.findByRole("status")).toHaveTextContent("密码重置邮件已发送");
  });

  it("shows why a sign-in failed and lets the person try again", async () => {
    const props = setup({ error: "邮箱或密码错误", onSignIn: vi.fn().mockRejectedValue(new Error("x")) });
    expect(screen.getByRole("alert")).toHaveTextContent("邮箱或密码错误");
    fill("me@example.com", "wrong");
    fireEvent.click(screen.getByRole("button", { name: "登录" }));
    await waitFor(() => expect(props.onSignIn).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole("button", { name: "登录" })).toBeEnabled());
  });
});
