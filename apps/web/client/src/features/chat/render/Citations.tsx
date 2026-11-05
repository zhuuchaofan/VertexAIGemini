import type { Citation } from "../../../api/types";

function host(uri: string): string {
  try {
    return new URL(uri).hostname.replace(/^www\./, "");
  } catch {
    return uri;
  }
}

/** Search grounding links go through Google's redirector, so their host says nothing about the real source. */
function showHost(item: Citation): boolean {
  const name = host(item.uri);
  return name !== "vertexaisearch.cloud.google.com" && Boolean(item.title) && item.title !== name;
}

function safe(uri: string): boolean {
  try {
    const { protocol } = new URL(uri);
    return protocol === "https:" || protocol === "http:";
  } catch {
    return false;
  }
}

export function Citations({ items }: { items: Citation[] }) {
  // Numbers follow the whole list, so [3] in the text is the third source even when an earlier one cannot be shown.
  const usable = items.map((item, number) => ({ item, number })).filter(({ item }) => safe(item.uri));
  if (usable.length === 0) return null;
  return (
    <section className="citations" aria-label="引用来源">
      <h4>来源 · {usable.length}</h4>
      <ol>
        {usable.map(({ item, number }) => (
          <li key={`${item.uri}-${number}`}>
            <a href={item.uri} target="_blank" rel="noopener noreferrer">
              <span className="cite-n">{number + 1}</span>
              <span className="cite-title">{item.title || host(item.uri)}</span>
              {showHost(item) && <span className="cite-host">{host(item.uri)}</span>}
            </a>
          </li>
        ))}
      </ol>
    </section>
  );
}
