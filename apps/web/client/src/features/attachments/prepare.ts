import { IMAGE_MAX_DIMENSION, MAX_IMAGE_UPLOAD_BYTES, normalizeMimeType, isImageType } from "./rules";

/** An attachment on its way out: content ready to send, and a local preview for an image. */
export interface PendingAttachment {
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  base64Data: string;
  /** An object URL for an image preview. Revoked when the attachment is removed or sent. */
  previewUrl?: string;
}

function readAsBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      const result = String(reader.result ?? "");
      resolve(result.includes(",") ? result.split(",", 2)[1] ?? "" : result);
    });
    reader.addEventListener("error", () => reject(reader.error ?? new Error("读取文件失败")));
    reader.readAsDataURL(blob);
  });
}

const canvasToBlob = (canvas: HTMLCanvasElement, type: string, quality: number) =>
  new Promise<Blob | null>(resolve => canvas.toBlob(resolve, type, quality));

const webpName = (name: string) => `${name.replace(/\.[^.]+$/, "") || "image"}.webp`;

/**
 * An image is re-encoded as WebP, longest side at most 1600 px, so it fits what a message can hold. If the browser
 * cannot do that (no canvas), a small enough original is sent as it is; a large one is refused with the reason.
 */
async function compressImage(file: File): Promise<{ blob: Blob; mimeType: string; fileName: string }> {
  if (typeof createImageBitmap !== "function") {
    if (file.size > MAX_IMAGE_UPLOAD_BYTES) throw new Error("这个浏览器无法压缩图片，请换一张更小的图片（700 KB 以内）。");
    return { blob: file, mimeType: normalizeMimeType(file), fileName: file.name };
  }

  const bitmap = await createImageBitmap(file);
  try {
    const scale = Math.min(1, IMAGE_MAX_DIMENSION / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    canvas.getContext("2d", { alpha: false })?.drawImage(bitmap, 0, 0, canvas.width, canvas.height);

    let blob = await canvasToBlob(canvas, "image/webp", 0.82);
    if (!blob || blob.size > MAX_IMAGE_UPLOAD_BYTES) blob = await canvasToBlob(canvas, "image/webp", 0.68);
    if (!blob) throw new Error("图片压缩失败。");
    if (blob.size > MAX_IMAGE_UPLOAD_BYTES) throw new Error("图片压缩后仍然过大，请裁剪或换一张更小的图片。");
    return { blob, mimeType: "image/webp", fileName: webpName(file.name) };
  } finally {
    bitmap.close?.();
  }
}

export async function prepareAttachment(file: File, mimeType: string, id: string): Promise<PendingAttachment> {
  if (isImageType(mimeType)) {
    const image = await compressImage(file);
    return {
      id,
      fileName: image.fileName,
      mimeType: image.mimeType,
      size: image.blob.size,
      base64Data: await readAsBase64(image.blob),
      previewUrl: URL.createObjectURL(image.blob)
    };
  }

  return { id, fileName: file.name, mimeType, size: file.size, base64Data: await readAsBase64(file) };
}

export function releasePreview(attachment: PendingAttachment) {
  if (attachment.previewUrl) URL.revokeObjectURL(attachment.previewUrl);
}
