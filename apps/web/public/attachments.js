// Attachment limits, validation, MIME normalisation and image compression.
export const MAX_ATTACHMENTS = 8;
export const MAX_SOURCE_IMAGE_BYTES = 12 * 1024 * 1024;
export const MAX_FILE_BYTES = 512 * 1024;
const MAX_IMAGE_UPLOAD_BYTES = 700 * 1024;
const IMAGE_MAX_DIMENSION = 1600;
const IMAGE_WEBP_QUALITY = 0.82;
const TEXT_FILE_EXTENSIONS = new Set([
  "csv", "cs", "go", "java", "json", "md", "markdown",
  "py", "rs", "sql", "txt", "yaml", "yml"
]);

export function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      const result = String(reader.result ?? "");
      resolve(result.includes(",") ? result.split(",", 2)[1] : result);
    });
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

export async function createCompressedImageAttachment(file) {
  const compressed = await compressImageToWebp(file);
  if (compressed.size > MAX_IMAGE_UPLOAD_BYTES) {
    throw new Error("图片压缩后仍然过大，请裁剪或选择更小的图片");
  }

  return {
    base64Data: await blobToBase64(compressed.blob),
    mimeType: compressed.mimeType,
    fileName: toWebpFileName(file.name)
  };
}

async function compressImageToWebp(file) {
  if (!supportsCanvasImageCompression()) {
    return {
      blob: file,
      mimeType: normalizeFileMimeType(file),
      size: file.size
    };
  }

  const bitmap = await createImageBitmap(file);
  try {
    const scale = Math.min(1, IMAGE_MAX_DIMENSION / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d", { alpha: false });
    context.drawImage(bitmap, 0, 0, width, height);

    let blob = await canvasToBlob(canvas, "image/webp", IMAGE_WEBP_QUALITY);
    if (!blob || blob.size > MAX_IMAGE_UPLOAD_BYTES) {
      blob = await canvasToBlob(canvas, "image/webp", 0.68);
    }
    if (!blob) {
      throw new Error("图片压缩失败");
    }

    return { blob, mimeType: "image/webp", size: blob.size };
  } finally {
    bitmap.close?.();
  }
}

function supportsCanvasImageCompression() {
  return typeof createImageBitmap === "function";
}

function canvasToBlob(canvas, mimeType, quality) {
  return new Promise(resolve => canvas.toBlob(resolve, mimeType, quality));
}

function blobToBase64(blob) {
  return readFileAsBase64(blob);
}

function toWebpFileName(fileName) {
  const baseName = fileName.replace(/\.[^.]+$/, "");
  return `${baseName || "image"}.webp`;
}

export function toDataUrl(image) {
  return `data:${image.mimeType};base64,${image.base64Data}`;
}

export function normalizeFileMimeType(file) {
  if (file.type) {
    return file.type;
  }

  const extension = getAttachmentExtension(file.name);
  if (extension === "pdf") return "application/pdf";
  if (extension === "json") return "application/json";
  if (extension === "csv") return "text/csv";
  if (extension === "md" || extension === "markdown") return "text/markdown";
  if (extension === "yaml" || extension === "yml") return "text/yaml";
  if (TEXT_FILE_EXTENSIONS.has(extension)) return "text/plain";
  return "application/octet-stream";
}

export function isAllowedAttachment(file, mimeType) {
  if (mimeType.startsWith("image/")) {
    return true;
  }

  if (mimeType === "application/pdf"
    || mimeType === "application/json"
    || mimeType === "application/x-yaml"
    || mimeType === "text/csv"
    || mimeType === "text/markdown"
    || mimeType === "text/plain"
    || mimeType === "text/yaml") {
    return true;
  }

  return TEXT_FILE_EXTENSIONS.has(getAttachmentExtension(file.name));
}

export function getAttachmentExtension(fileName = "") {
  const extension = fileName.split(".").pop() ?? "";
  return extension === fileName ? "" : extension.toLowerCase();
}

export function getAttachmentLabel(fileName = "") {
  return getAttachmentExtension(fileName).slice(0, 5).toUpperCase() || "FILE";
}

