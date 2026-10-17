import { useMemo, useState } from "react";
import { createApiClient } from "./api/client";
import { useSession } from "./auth/session";
import { ChatView } from "./features/chat/ChatView";
import { ConversationList } from "./features/conversations/ConversationList";
import { LoginForm } from "./LoginForm";
import { useRoute } from "./route";

/**
 * Batch 1: the smallest shell that runs the whole send, stream, stop and recover chain for real.
 * Layout, settings, attachments and the rest of the screens follow in batch 2.
 */
export function App() {
  const session = useSession();
  const route = useRoute();
  const api = useMemo(() => createApiClient(session.headers), [session.headers]);
  // A new conversation gets its id up front, so even its first message can be sent again safely.
  const [fresh, setFresh] = useState(() => crypto.randomUUID());
  const [listVersion, setListVersion] = useState(0);

  if (session.status === "loading") {
    return <main className="boot"><p>正在进入工作台…</p></main>;
  }

  if (session.status === "unavailable") {
    return <main className="boot"><h1>球球布丁工作室</h1><p className="note err" role="alert">{session.error}</p></main>;
  }

  if (session.status === "signed-out") {
    return <LoginForm error={session.error} onSubmit={session.signIn} />;
  }

  const conversationId = route.conversationId ?? fresh;

  return (
    <div className="shell">
      <ConversationList
        api={api}
        activeId={route.conversationId}
        version={listVersion}
        onOpen={route.openConversation}
        onNew={() => {
          setFresh(crypto.randomUUID());
          route.newConversation();
        }}
      />
      <main className="main">
        <header className="bar">
          <span>{session.email}</span>
          <button type="button" className="link" onClick={() => void session.signOut()}>退出</button>
        </header>
        <ChatView
          key={conversationId}
          api={api}
          conversationId={conversationId}
          onRunAdmitted={() => {
            if (!route.conversationId) route.openConversation(conversationId);
          }}
          onRunEnded={run => {
            setListVersion(v => v + 1);
            if (!route.conversationId) route.openConversation(run.conversationId);
          }}
        />
      </main>
    </div>
  );
}
