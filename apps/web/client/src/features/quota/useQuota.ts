import { useEffect, useState } from "react";
import type { QuotaStatus, RunApi } from "../../api/types";

/** Today's allowance. Read again whenever `version` changes (a run ended). A failed read just hides the bar. */
export function useQuota(api: Pick<RunApi, "getQuota">, version: number): QuotaStatus | null {
  const [status, setStatus] = useState<QuotaStatus | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .getQuota()
      .then(next => {
        if (!cancelled) setStatus(next);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [api, version]);

  return status;
}
