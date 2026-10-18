#!/usr/bin/env python3
"""Checks every index in firestore.indexes.json against `gcloud firestore indexes composite list --format=json`.

An index counts only if collection group, query scope and the full ordered field list (with each field's order or
array config) match a live index whose state is READY. Several indexes can share a collection group and prefix, so
matching by collection name alone is not enough.

  gcloud firestore indexes composite list --project=P --format=json | check-firestore-indexes.py [manifest]

Prints one line per manifest index; exits 0 only when all are READY.
"""
import json
import sys


def signature(fields, drop_name=True):
    result = []
    for field in fields:
        if drop_name and field.get("fieldPath") == "__name__":
            continue
        result.append((field["fieldPath"], field.get("order") or field.get("arrayConfig") or ""))
    return tuple(result)


def live_collection(name):
    marker = "/collectionGroups/"
    return name.split(marker, 1)[1].split("/", 1)[0] if marker in name else ""


def check(manifest, live):
    """Returns (lines, ok)."""
    lines, ok = [], True
    for wanted in manifest.get("indexes", []):
        want_sig = signature(wanted["fields"], drop_name=False)
        scope = wanted.get("queryScope", "COLLECTION")
        label = "%s (%s)" % (wanted["collectionGroup"], ", ".join("%s %s" % pair for pair in want_sig))
        matches = [
            item for item in live
            if live_collection(item.get("name", "")) == wanted["collectionGroup"]
            and item.get("queryScope", "COLLECTION") == scope
            and signature(item.get("fields", []), drop_name=not any(f["fieldPath"] == "__name__" for f in wanted["fields"])) == want_sig
        ]
        if any(item.get("state") == "READY" for item in matches):
            lines.append("  ready    " + label)
        elif matches:
            lines.append("  %-8s %s" % (matches[0].get("state", "unknown").lower(), label))
            ok = False
        else:
            lines.append("  missing  " + label)
            ok = False
    return lines, ok


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "firestore.indexes.json"
    with open(path) as handle:
        manifest = json.load(handle)
    text = sys.stdin.read().strip()
    live = json.loads(text) if text else []
    lines, ok = check(manifest, live)
    print("\n".join(lines))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
