import { useState } from "react";
import { useUpdateAvailable } from "./useUpdateAvailable";

/** A quiet note that a new version is ready. Nothing reloads until the person taps. */
export function UpdateBanner() {
  const available = useUpdateAvailable();
  const [dismissed, setDismissed] = useState(false);
  if (!available || dismissed) return null;
  return (
    <div className="update-banner" role="status">
      <span>🍮 有新版本啦</span>
      <button type="button" className="update-go" onClick={() => window.location.reload()}>点此刷新</button>
      <button type="button" className="update-later" aria-label="稍后再说" onClick={() => setDismissed(true)}>×</button>
    </div>
  );
}
