import { useState } from "react";

interface Props {
  /** The reason the last attempt failed, from the session. */
  error: string | null;
  onSignIn: (email: string, password: string) => Promise<void>;
  onSignUp: (email: string, password: string) => Promise<void>;
  onGoogle: () => Promise<void>;
  onReset: (email: string) => Promise<void>;
}

type Mode = "login" | "register";

/** Email and password (sign in or register), Google, and a password reset mail: everything the old page offered. */
export function LoginForm({ error, onSignIn, onSignUp, onGoogle, onReset }: Props) {
  const [mode, setMode] = useState<Mode>("login");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [info, setInfo] = useState<string | null>(null);
  const [localError, setLocalError] = useState<string | null>(null);

  const run = async (action: () => Promise<void>, done?: string) => {
    setBusy(true);
    setInfo(null);
    setLocalError(null);
    try {
      await action();
      if (done) setInfo(done);
    } catch {
      // the session already exposes the reason as `error`
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="boot">
      <div className="boot-mark" aria-hidden="true">球</div>
      <h1>球球布丁工作室</h1>
      <p className="muted">登录后进入聊天工作台</p>

      <div className="segmented login-tabs" role="tablist" aria-label="登录方式">
        {(["login", "register"] as const).map(value => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={mode === value}
            className={mode === value ? "seg on" : "seg"}
            onClick={() => {
              setMode(value);
              setInfo(null);
              setLocalError(null);
            }}
          >
            {value === "login" ? "登录" : "注册"}
          </button>
        ))}
      </div>

      <form
        className="login"
        onSubmit={event => {
          event.preventDefault();
          void run(() => (mode === "login" ? onSignIn(email.trim(), password) : onSignUp(email.trim(), password)));
        }}
      >
        <label>
          邮箱
          <input type="email" autoComplete="email" required value={email} onChange={e => setEmail(e.target.value)} />
        </label>
        <label>
          密码
          <input
            type="password"
            autoComplete={mode === "login" ? "current-password" : "new-password"}
            required
            minLength={mode === "register" ? 6 : undefined}
            value={password}
            onChange={e => setPassword(e.target.value)}
          />
        </label>
        <button type="submit" disabled={busy}>{mode === "login" ? "登录" : "注册并登录"}</button>
        <button type="button" className="ghost" disabled={busy} onClick={() => void run(onGoogle)}>
          使用 Google 账号继续
        </button>
        {mode === "login" && (
          <button
            type="button"
            className="link"
            disabled={busy}
            onClick={() => {
              if (!email.trim()) {
                setLocalError("请输入邮箱后再重置密码");
                return;
              }
              void run(() => onReset(email.trim()), "密码重置邮件已发送，请检查邮箱");
            }}
          >
            忘记密码？
          </button>
        )}
        {(localError ?? error) && <p className="note err" role="alert">{localError ?? error}</p>}
        {info && <p className="note info" role="status">{info}</p>}
      </form>
    </main>
  );
}
