#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const project = process.env.FIRESTORE_PROJECT_ID
  || process.env.FIREBASE_PROJECT_ID
  || process.env.PROJECT_ID;
const manifestPath = resolve(process.argv[2] ?? "firestore.indexes.json");

if (!project) {
  console.error("[ERROR] FIRESTORE_PROJECT_ID, FIREBASE_PROJECT_ID, or PROJECT_ID is required.");
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
const indexes = Array.isArray(manifest.indexes) ? manifest.indexes : [];

for (const index of indexes) {
  const fields = Array.isArray(index.fields) ? index.fields : [];
  const args = [
    "firestore",
    "indexes",
    "composite",
    "create",
    `--project=${project}`,
    `--collection-group=${index.collectionGroup}`,
    `--query-scope=${index.queryScope ?? "COLLECTION"}`
  ];

  for (const field of fields) {
    if (field.order) {
      args.push(`--field-config=field-path=${field.fieldPath},order=${field.order}`);
    } else if (field.arrayConfig) {
      args.push(`--field-config=field-path=${field.fieldPath},array-config=${field.arrayConfig}`);
    }
  }

  console.log(`[INFO] Ensuring Firestore index ${index.collectionGroup}: ${fields.map(formatField).join(", ")}`);
  const result = spawnSync("gcloud", args, {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
  if (result.status === 0) {
    if (result.stdout) process.stdout.write(result.stdout);
    continue;
  }

  if (isAlreadyExists(output)) {
    console.log("[INFO] Index already exists.");
    continue;
  }

  process.stderr.write(output);
  process.exit(result.status ?? 1);
}

console.log(`[INFO] Firestore index deployment requested for ${indexes.length} index(es).`);

function formatField(field) {
  return field.order
    ? `${field.fieldPath}:${field.order}`
    : `${field.fieldPath}:${field.arrayConfig}`;
}

function isAlreadyExists(output) {
  return /ALREADY_EXISTS|already exists/i.test(output);
}
