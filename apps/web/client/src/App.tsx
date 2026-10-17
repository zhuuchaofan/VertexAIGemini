import { useMemo, useState } from "react";
import { createApiClient } from "./api/client";
import type { WorkspaceConfig } from "./api/types";
import { useSession } from "./auth/session";
import { ChatView } from "./features/chat/ChatView";
import { Sidebar } from "./features/conversations/Sidebar";
import { useDefaults } from "./features/workspace/useDefaults";
import { useWorkspace } from "./features/workspace/useWorkspace";
import { LoginForm } from "./LoginForm";
import { useRoute } from "./route";
import { useTheme } from "./theme/useTheme";

export function App() {
  const session = useSession();
  const route = useRoute();
  const theme = useTheme();
  const api = useMemo(() => createApiClient(session.headers), [session.headers]);
  // A new conversation gets its id up front, so even its first message can be sent again safely.
  const [fresh, setFresh] = useState(() => crypto.randomUUID());
  const [listVersion, setListVersion] = useState(0);
  const [drawer, setDrawer] = useState(false);

  if (session.status === "loading") {
    return <main className="boot"><p>正在进入工作台…</p></main>;
  }

  if (session.status === "unavailable") {
    return <main className="boot"><h1>球球布丁工作室</h1><p className="note err" role="alert">{session.error}</p></main>;
  }

  if (session.status === "signed-out") {
    return <LoginForm error={session.error} onSubmit={session.signIn} />;
  }

  return (
    <Workbench
      key={session.email ?? "signed-in"}
      api={api}
      email={session.email}
      onSignOut={() => void session.signOut()}
      route={route}
      theme={theme}
      fresh={fresh}
      onNew={() => {
        setFresh(crypto.randomUUID());
        route.newConversation();
      }}
      listVersion={listVersion}
      bumpList={() => setListVersion(version => version + 1)}
      drawer={drawer}
      setDrawer={setDrawer}
    />
  );
}

interface WorkbenchProps {
  api: ReturnType<typeof createApiClient>;
  email: string | null;
  onSignOut: () => void;
  route: ReturnType<typeof useRoute>;
  theme: ReturnType<typeof useTheme>;
  fresh: string;
  onNew: () => void;
  listVersion: number;
  bumpList: () => void;
  drawer: boolean;
  setDrawer: (open: boolean) => void;
}

function Workbench(props: WorkbenchProps) {
  const state = useWorkspace(props.api);

  if (state.status === "loading") {
    return <main className="boot"><p>正在读取工作台配置…</p></main>;
  }
  if (state.status === "error") {
    return <main className="boot"><h1>球球布丁工作室</h1><p className="note err" role="alert">{state.error}</p></main>;
  }

  return <ReadyWorkbench {...props} workspace={state.workspace} safetyThresholds={state.auth.safetyThresholds} accountEmail={state.auth.email} />;
}

interface ReadyProps extends WorkbenchProps {
  workspace: WorkspaceConfig;
  safetyThresholds: string[];
  accountEmail: string;
}

function ReadyWorkbench({ api, email, onSignOut, route, theme, fresh, onNew, listVersion, bumpList, drawer, setDrawer, workspace, safetyThresholds, accountEmail }: ReadyProps) {
  const defaults = useDefaults(api, workspace);
  if (defaults.status === "loading") {
    return <main className="boot"><p>正在读取你的默认设置…</p></main>;
  }

  const conversationId = route.conversationId ?? fresh;

  return (
    <div className="shell">
      <Sidebar
        api={api}
        activeId={route.conversationId}
        version={listVersion}
        email={accountEmail || email}
        theme={theme.choice}
        onCycleTheme={theme.cycle}
        onOpen={route.openConversation}
        onNew={onNew}
        onSignOut={onSignOut}
        open={drawer}
        onClose={() => setDrawer(false)}
      />
      <main className="main">
        <ChatView
          key={conversationId}
          api={api}
          conversationId={conversationId}
          workspace={workspace}
          defaults={defaults.defaults}
          onSaveDefaults={defaults.save}
          safetyThresholds={safetyThresholds}
          onMenu={() => setDrawer(true)}
          onRunAdmitted={() => {
            if (!route.conversationId) route.openConversation(conversationId);
          }}
          onRunEnded={run => {
            bumpList();
            if (!route.conversationId) route.openConversation(run.conversationId);
          }}
        />
      </main>
    </div>
  );
}
