import { useEffect, useState } from "react";
import type { AuthStatus, RunApi, WorkspaceConfig } from "../../api/types";

export type WorkspaceState =
  | { status: "loading" }
  | { status: "error"; error: string }
  | { status: "ready"; workspace: WorkspaceConfig; auth: AuthStatus };

/** The model catalogue and what this account may use. Loaded once per sign-in. */
export function useWorkspace(api: RunApi): WorkspaceState {
  const [state, setState] = useState<WorkspaceState>({ status: "loading" });

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.getWorkspace(), api.getAuthStatus()])
      .then(([workspace, auth]) => {
        if (!cancelled) setState({ status: "ready", workspace, auth });
      })
      .catch(error => {
        if (!cancelled) setState({ status: "error", error: error instanceof Error ? error.message : "无法读取工作台配置。" });
      });
    return () => {
      cancelled = true;
    };
  }, [api]);

  return state;
}
