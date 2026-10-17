import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AuthHeaders } from "../api/client";

export type SessionStatus = "loading" | "signed-out" | "signed-in" | "unavailable";

export interface Session {
  status: SessionStatus;
  email: string | null;
  error: string | null;
  headers: AuthHeaders;
  signIn(email: string, password: string): Promise<void>;
  signOut(): Promise<void>;
}

interface FirebaseClientConfig {
  apiKey: string;
  authDomain?: string | null;
  projectId: string;
  appId?: string | null;
}

interface FirebaseAuthModule {
  auth: import("firebase/auth").Auth;
  mod: typeof import("firebase/auth");
}

/**
 * Signs the user in and supplies request credentials. The browser only ever holds a Firebase ID token;
 * model credentials never leave the server. A development build with VITE_DEV_USER set talks to an API that runs
 * with Dev:FakeUser instead of Firebase.
 */
export function useSession(): Session {
  const devUser = import.meta.env.VITE_DEV_USER as string | undefined;
  const [status, setStatus] = useState<SessionStatus>(devUser ? "signed-in" : "loading");
  const [email, setEmail] = useState<string | null>(devUser ? `${devUser}@dev.local` : null);
  const [error, setError] = useState<string | null>(null);
  const firebase = useRef<FirebaseAuthModule | null>(null);

  useEffect(() => {
    if (devUser) return; // a development build is signed in from the start

    let unsubscribe: (() => void) | undefined;
    let cancelled = false;

    (async () => {
      try {
        const response = await fetch("/api/workspace/config");
        const config = (await response.json()) as { firebase: FirebaseClientConfig | null };
        if (!config.firebase) throw new Error("服务端没有配置 Firebase 登录。");

        const [{ initializeApp }, mod] = await Promise.all([import("firebase/app"), import("firebase/auth")]);
        const app = initializeApp({
          apiKey: config.firebase.apiKey,
          authDomain: config.firebase.authDomain ?? undefined,
          projectId: config.firebase.projectId,
          appId: config.firebase.appId ?? undefined
        });
        const auth = mod.getAuth(app);
        firebase.current = { auth, mod };
        unsubscribe = mod.onAuthStateChanged(auth, user => {
          if (cancelled) return;
          setEmail(user?.email ?? null);
          setStatus(user ? "signed-in" : "signed-out");
        });
      } catch (caught) {
        if (cancelled) return;
        setError(caught instanceof Error ? caught.message : "无法初始化登录。");
        setStatus("unavailable");
      }
    })();

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, [devUser]);

  const headers = useCallback<AuthHeaders>(async (): Promise<Record<string, string>> => {
    if (devUser) return { "X-Dev-User": devUser };
    const current = firebase.current?.auth.currentUser;
    return current ? { Authorization: `Bearer ${await current.getIdToken()}` } : {};
  }, [devUser]);

  const signIn = useCallback(async (address: string, password: string) => {
    const loaded = firebase.current;
    if (!loaded) throw new Error("登录尚未就绪。");
    setError(null);
    try {
      await loaded.mod.signInWithEmailAndPassword(loaded.auth, address, password);
    } catch (caught) {
      const message = caught instanceof Error ? caught.message : "登录失败。";
      setError(message);
      throw caught;
    }
  }, []);

  const signOut = useCallback(async () => {
    if (devUser) return;
    const loaded = firebase.current;
    if (loaded) await loaded.mod.signOut(loaded.auth);
  }, [devUser]);

  return useMemo(
    () => ({ status, email, error, headers, signIn, signOut }),
    [status, email, error, headers, signIn, signOut]
  );
}
