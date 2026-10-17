import type { Citation } from "../../../api/types";

function host(uri: string): string {
  try {
    return new URL(uri).hostname.replace(/^www\./, "");
  } catch {
    return uri;
  }
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
  const usable = items.filter(item => safe(item.uri));
  if (usable.length === 0) return null;
  return (
    <section className="citations" aria-label="引用来源">
      <h4>来源 · {usable.length}</h4>
      <ol>
        {usable.map((item, index) => (
          <li key={`${item.uri}-${index}`}>
            <a href={item.uri} target="_blank" rel="noopener noreferrer">
              <span className="cite-n">{index + 1}</span>
              <span className="cite-title">{item.title || host(item.uri)}</span>
              <span className="cite-host">{host(item.uri)}</span>
            </a>
          </li>
        ))}
      </ol>
    </section>
  );
}
