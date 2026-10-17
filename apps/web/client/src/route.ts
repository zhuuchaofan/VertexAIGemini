import { useCallback, useEffect, useState } from "react";

const BASE = "/app/";

/** The conversation id in `/app/c/<id>`, or null for a new conversation at `/app/`. */
export function parseConversationId(pathname: string): string | null {
  const match = /^\/app\/c\/([0-9a-fA-F-]{36})\/?$/.exec(pathname);
  return match?.[1] ?? null;
}

export function useRoute() {
  const [pathname, setPathname] = useState(() => window.location.pathname);

  useEffect(() => {
    const update = () => setPathname(window.location.pathname);
    window.addEventListener("popstate", update);
    return () => window.removeEventListener("popstate", update);
  }, []);

  const navigate = useCallback((path: string) => {
    window.history.pushState(null, "", path);
    setPathname(path);
  }, []);

  return {
    conversationId: parseConversationId(pathname),
    openConversation: (id: string) => navigate(`${BASE}c/${id}`),
    newConversation: () => navigate(BASE)
  };
}
