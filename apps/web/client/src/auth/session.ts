import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AuthHeaders } from "../api/client";
import { authErrorMessage } from "./messages";

export type SessionStatus = "loading" | "signed-out" | "signed-in" | "unavailable";

export interface Session {
  status: SessionStatus;
  email: string | null;
  error: string | null;
  headers: AuthHeaders;
  signIn(email: string, password: string): Promise<void>;
  signUp(email: string, password: string): Promise<void>;
  signInWithGoogle(): Promise<void>;
  /** Resolves when the reset mail was sent. */
  resetPassword(email: string): Promise<void>;
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
 * The development user, only in a build made for one (VITE_DEV_USER set). End-to-end tests give each test its own
 * user through local storage, so they cannot see each other's conversations or settings. Production builds never read it.
 */
function readDevUser(): string | undefined {
  const fixed = import.meta.env.VITE_DEV_USER as string | undefined;
  if (!fixed) return undefined;
  try {
    return localStorage.getItem("e2e:user") ?? fixed;
  } catch {
    return fixed;
  }
}

/**
 * Signs the user in and supplies request credentials. The browser only ever holds a Firebase ID token;
 * model credentials never leave the server. A development build with VITE_DEV_USER set talks to an API that runs
 * with Dev:FakeUser instead of Firebase.
 */
export function useSession(): Session {
  const devUser = readDevUser();
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

  // Each way in reports its failure in plain language through `error`, and rethrows so a form can stop its spinner.
  const attempt = useCallback(async (action: (loaded: FirebaseAuthModule) => Promise<unknown>) => {
    const loaded = firebase.current;
    if (!loaded) throw new Error("登录尚未就绪。");
    setError(null);
    try {
      await action(loaded);
    } catch (caught) {
      setError(authErrorMessage(caught));
      throw caught;
    }
  }, []);

  const signIn = useCallback(
    (address: string, password: string) => attempt(l => l.mod.signInWithEmailAndPassword(l.auth, address, password)),
    [attempt]
  );
  const signUp = useCallback(
    (address: string, password: string) => attempt(l => l.mod.createUserWithEmailAndPassword(l.auth, address, password)),
    [attempt]
  );
  const signInWithGoogle = useCallback(
    () =>
      attempt(l => {
        const provider = new l.mod.GoogleAuthProvider();
        provider.setCustomParameters({ prompt: "select_account" });
        return l.mod.signInWithPopup(l.auth, provider);
      }),
    [attempt]
  );
  const resetPassword = useCallback(
    (address: string) => attempt(l => l.mod.sendPasswordResetEmail(l.auth, address)),
    [attempt]
  );

  const signOut = useCallback(async () => {
    if (devUser) return;
    const loaded = firebase.current;
    if (loaded) await loaded.mod.signOut(loaded.auth);
  }, [devUser]);

  return useMemo(
    () => ({ status, email, error, headers, signIn, signUp, signInWithGoogle, resetPassword, signOut }),
    [status, email, error, headers, signIn, signUp, signInWithGoogle, resetPassword, signOut]
  );
}
