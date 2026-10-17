import { useCallback, useEffect, useState } from "react";
import type { RunApi, WorkspaceConfig } from "../../api/types";
import { defaultSettings, fromStored, toStored, type ChatSettings } from "../settings/chatSettings";

export type DefaultsState =
  | { status: "loading" }
  | { status: "ready"; defaults: ChatSettings; save: (settings: ChatSettings) => Promise<void> };

/**
 * "My defaults": what a new conversation starts with. If none were saved, or the stored ones cannot be read, the
 * catalogue's own defaults are used; the page never waits on or breaks because of them.
 */
export function useDefaults(api: RunApi, workspace: WorkspaceConfig): DefaultsState {
  const [defaults, setDefaults] = useState<ChatSettings | null>(null);

  useEffect(() => {
    let cancelled = false;
    const fallback = defaultSettings(workspace);
    api
      .getDefaults()
      .then(stored => {
        if (!cancelled) setDefaults(fromStored(workspace, stored, fallback));
      })
      .catch(() => {
        if (!cancelled) setDefaults(fallback);
      });
    return () => {
      cancelled = true;
    };
  }, [api, workspace]);

  const save = useCallback(
    async (settings: ChatSettings) => {
      const stored = await api.putDefaults(toStored(workspace, settings, { includePrompt: false }));
      setDefaults(fromStored(workspace, stored, defaultSettings(workspace)));
    },
    [api, workspace]
  );

  return defaults ? { status: "ready", defaults, save } : { status: "loading" };
}
