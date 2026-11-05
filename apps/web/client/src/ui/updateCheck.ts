/** The fingerprinted entry script in an HTML page, e.g. `/app/assets/index-AbC123.js`, or null if there is none. */
export function entryScript(html: string): string | null {
  const match = /<script[^>]+type="module"[^>]+src="([^"]*\/assets\/index-[^"]+\.js)"/.exec(html) ?? /<script[^>]+src="([^"]*\/assets\/index-[^"]+\.js)"[^>]+type="module"/.exec(html);
  return match?.[1] ?? null;
}

/** The entry script this page is running now, as a path. */
export function runningEntryScript(doc: Document = document): string | null {
  const script = Array.from(doc.querySelectorAll<HTMLScriptElement>('script[type="module"][src]')).find(item => /\/assets\/index-[^/]+\.js$/.test(item.src));
  return script ? new URL(script.src, doc.baseURI).pathname : null;
}

/** True when the server now serves a different build than the one running. Unknown (null) counts as no. */
export function isNewer(running: string | null, served: string | null): boolean {
  if (!running || !served) return false;
  return new URL(served, "https://x.invalid").pathname !== running;
}
