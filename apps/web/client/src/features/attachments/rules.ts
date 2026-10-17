// What may be attached, decided in the browser first so a person hears why a file was not added before anything is sent.
// The server enforces the same limits again; these are never the only check.

export const MAX_ATTACHMENTS = 8;
export const MAX_FILE_BYTES = 512 * 1024;
export const MAX_SOURCE_IMAGE_BYTES = 12 * 1024 * 1024;
/** Images are re-encoded to fit this; it is also what fits inside one stored message without a bucket. */
export const MAX_IMAGE_UPLOAD_BYTES = 700 * 1024;
export const MAX_IMAGE_SERVER_BYTES = 4 * 1024 * 1024;
export const IMAGE_MAX_DIMENSION = 1600;

const TEXT_EXTENSIONS = new Set(["csv", "cs", "go", "java", "json", "md", "markdown", "py", "rs", "sql", "txt", "yaml", "yml"]);

const ALLOWED_FILE_TYPES = new Set([
  "application/pdf",
  "application/json",
  "application/x-yaml",
  "text/csv",
  "text/markdown",
  "text/plain",
  "text/yaml"
]);

const IMAGE_TYPES = new Set(["image/jpeg", "image/png", "image/webp", "image/gif"]);

export interface FileLike {
  name: string;
  type: string;
  size: number;
}

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot < 0 || dot === name.length - 1 ? "" : name.slice(dot + 1).toLowerCase();
}

/** The label shown on a file chip, from its extension. */
export function labelOf(name: string): string {
  return extensionOf(name).slice(0, 5).toUpperCase() || "FILE";
}

/** The type to send. Browsers often leave it empty for source files, so the extension fills the gap. */
export function normalizeMimeType(file: FileLike): string {
  if (file.type) return file.type.toLowerCase();
  const extension = extensionOf(file.name);
  if (extension === "pdf") return "application/pdf";
  if (extension === "json") return "application/json";
  if (extension === "csv") return "text/csv";
  if (extension === "md" || extension === "markdown") return "text/markdown";
  if (extension === "yaml" || extension === "yml") return "text/yaml";
  if (TEXT_EXTENSIONS.has(extension)) return "text/plain";
  return "application/octet-stream";
}

export const isImageType = (mimeType: string) => mimeType.startsWith("image/");

export function isAllowed(file: FileLike, mimeType: string): boolean {
  if (IMAGE_TYPES.has(mimeType)) return true;
  if (ALLOWED_FILE_TYPES.has(mimeType)) return true;
  return TEXT_EXTENSIONS.has(extensionOf(file.name));
}

export interface Rejection {
  name: string;
  reason: string;
}

export interface Selection<T extends FileLike> {
  accepted: { file: T; mimeType: string }[];
  rejected: Rejection[];
}

export const formatSize = (bytes: number): string =>
  bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;

/**
 * Splits a batch of files into what can be attached and what cannot, each refusal with its reason. Files beyond the
 * free slots are refused in order, never silently dropped.
 */
export function selectFiles<T extends FileLike>(files: readonly T[], alreadyAttached: number): Selection<T> {
  const accepted: Selection<T>["accepted"] = [];
  const rejected: Rejection[] = [];
  let slots = Math.max(0, MAX_ATTACHMENTS - alreadyAttached);

  for (const file of files) {
    const mimeType = normalizeMimeType(file);
    if (!isAllowed(file, mimeType)) {
      rejected.push({ name: file.name, reason: "不支持这种格式。可以添加图片、PDF、文本、CSV、JSON、Markdown 和常见源码文件。" });
      continue;
    }

    const image = isImageType(mimeType);
    const limit = image ? MAX_SOURCE_IMAGE_BYTES : MAX_FILE_BYTES;
    if (file.size > limit) {
      rejected.push({
        name: file.name,
        reason: image ? `超过 ${formatSize(MAX_SOURCE_IMAGE_BYTES)}（图片原图上限）。` : `超过 ${formatSize(MAX_FILE_BYTES)}（文件上限）。`
      });
      continue;
    }

    if (slots === 0) {
      rejected.push({ name: file.name, reason: `最多 ${MAX_ATTACHMENTS} 个附件。` });
      continue;
    }

    slots -= 1;
    accepted.push({ file, mimeType });
  }

  return { accepted, rejected };
}
