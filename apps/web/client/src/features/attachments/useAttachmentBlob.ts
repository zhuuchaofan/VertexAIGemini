import { useEffect, useState } from "react";
import type { RunApi } from "../../api/types";

type BlobState = { status: "loading" } | { status: "ready"; url: string } | { status: "error" };

/**
 * Loads one stored attachment with the person's own credentials (an <img> tag cannot send them) and turns it into a
 * local URL that is released when the view goes away.
 */
export function useAttachmentBlob(api: RunApi, conversationId: string, messageId: string, index: number, enabled: boolean): BlobState {
  const [state, setState] = useState<BlobState>({ status: "loading" });

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    let created: string | null = null;
    api
      .getAttachment(conversationId, messageId, index)
      .then(blob => {
        created = URL.createObjectURL(blob);
        if (cancelled) {
          URL.revokeObjectURL(created);
          return;
        }
        setState({ status: "ready", url: created });
      })
      .catch(() => {
        if (!cancelled) setState({ status: "error" });
      });
    return () => {
      cancelled = true;
      if (created) URL.revokeObjectURL(created);
    };
  }, [api, conversationId, messageId, index, enabled]);

  return enabled ? state : { status: "error" };
}
