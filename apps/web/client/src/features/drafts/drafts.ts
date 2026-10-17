// Unsent text and attachments, kept on this device only. Text lives in local storage (small, synchronous, lets the
// list show a "draft" tag); attachments live in IndexedDB, which can hold files. Both are best effort: a browser
// that refuses storage just has no drafts, and nothing else changes.

const PREFIX = "draft:";
const DB_NAME = "vertex-drafts";
const STORE = "attachments";

export interface DraftFile {
  fileName: string;
  mimeType: string;
  size: number;
  base64Data: string;
}

export interface Draft {
  text: string;
  files: DraftFile[];
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === "undefined") {
      reject(new Error("no indexedDB"));
      return;
    }
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const request = run(db.transaction(STORE, mode).objectStore(STORE));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  } finally {
    db.close();
  }
}

export async function loadDraft(key: string): Promise<Draft | null> {
  let text = "";
  let files = 0;
  try {
    const raw = localStorage.getItem(PREFIX + key);
    if (raw) {
      const parsed = JSON.parse(raw) as { text?: string; files?: number };
      text = parsed.text ?? "";
      files = parsed.files ?? 0;
    }
  } catch {
    // unreadable: no draft
  }

  let stored: DraftFile[] = [];
  if (files > 0) {
    try {
      stored = ((await withStore("readonly", store => store.get(key))) as DraftFile[] | undefined) ?? [];
    } catch {
      stored = [];
    }
  }

  return text || stored.length > 0 ? { text, files: stored } : null;
}

export async function saveDraft(key: string, draft: Draft): Promise<void> {
  if (!draft.text && draft.files.length === 0) {
    await clearDraft(key);
    return;
  }

  let kept = draft.files.length;
  if (draft.files.length > 0) {
    try {
      await withStore("readwrite", store => store.put(draft.files, key));
    } catch {
      kept = 0; // the text is still kept
    }
  } else {
    await dropFiles(key);
  }

  try {
    localStorage.setItem(PREFIX + key, JSON.stringify({ text: draft.text, files: kept, at: Date.now() }));
  } catch {
    // storage full or blocked
  }
}

async function dropFiles(key: string) {
  try {
    await withStore("readwrite", store => store.delete(key));
  } catch {
    // nothing stored, or no IndexedDB
  }
}

export async function clearDraft(key: string): Promise<void> {
  try {
    localStorage.removeItem(PREFIX + key);
  } catch {
    // ignore
  }
  await dropFiles(key);
}

/** Conversations (and "new") that have an unsent draft on this device. */
export function listDraftIds(): Set<string> {
  const ids = new Set<string>();
  try {
    for (let index = 0; index < localStorage.length; index += 1) {
      const name = localStorage.key(index);
      if (name?.startsWith(PREFIX)) ids.add(name.slice(PREFIX.length));
    }
  } catch {
    // none
  }
  return ids;
}
