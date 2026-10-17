import { useState } from "react";

interface Props {
  error: string | null;
  onSubmit: (email: string, password: string) => Promise<void>;
}

export function LoginForm({ error, onSubmit }: Props) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);

  return (
    <main className="boot">
      <div className="boot-mark" aria-hidden="true">球</div>
      <h1>球球布丁工作室</h1>
      <form
        className="login"
        onSubmit={async event => {
          event.preventDefault();
          setBusy(true);
          try {
            await onSubmit(email, password);
          } catch {
            // the session already exposes the error
          } finally {
            setBusy(false);
          }
        }}
      >
        <label>
          邮箱
          <input type="email" autoComplete="email" required value={email} onChange={e => setEmail(e.target.value)} />
        </label>
        <label>
          密码
          <input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} />
        </label>
        <button type="submit" disabled={busy}>登录</button>
        {error && <p className="note err" role="alert">{error}</p>}
      </form>
    </main>
  );
}
