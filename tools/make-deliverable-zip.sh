#!/usr/bin/env bash
# ============================================================================
# DE-CHIMEX PRINT HUB - deliverable packaging (single entry point).
# See AI_HANDOFF.txt sections 31 & 42.
#
#   Usage:  tools/make-deliverable-zip.sh <output.zip>
#
# OWNER DIRECTIVE (2026-09-05): the Brevo + Paystack TEST keys are
# intentionally committed in appsettings.json / appsettings.Development.json
# and MUST ship in every deliverable zip. NEVER exclude appsettings*.json.
#
# The zip carries ONLY git-tracked files (bin/ obj/ .git/ .vs/ logs/ runtime
# uploads are never tracked), laid out like the hand-off zips:
#     <zip root>/DeChimexPrints/...   +   <zip root>/AI_HANDOFF.txt
# ============================================================================
set -euo pipefail

out="${1:?usage: tools/make-deliverable-zip.sh <output.zip>}"
case "$out" in /*) ;; *) out="$PWD/$out";; esac

root="$(cd "$(dirname "$0")/.." && pwd)"
handoff="$(cd "$root/.." && pwd)"  # folder carrying DeChimexPrints/ + AI_HANDOFF.txt
# The git worktree root is either the solution folder itself (DeChimexPrints-v2
# layout), the hand-off folder (v036-UnsubscribeLinkOrigin layout, where the repo
# root also git-tracks AI_HANDOFF.txt beside DeChimexPrints/), or an ancestor of
# the hand-off folder (DeChimexNotificationsFixed layout, where the repo root
# tracks EmailDesign/ as a subfolder).
top="$(git -C "$root" rev-parse --show-toplevel 2>/dev/null)" \
  || { echo "REFUSING: $root is not inside a git worktree"; exit 1; }
git -C "$top" diff --quiet          || { echo "REFUSING: unstaged changes - commit first"; exit 1; }
git -C "$top" diff --cached --quiet || { echo "REFUSING: staged-but-uncommitted changes - commit first"; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

if [ "$top" = "$root" ]; then
  # Solution folder IS the git root: wrap it so the zip carries DeChimexPrints/.
  mkdir -p "$stage/DeChimexPrints"
  git -C "$top" archive --format=tar HEAD | tar -C "$stage/DeChimexPrints" -xf -
elif [ "$top" = "$handoff" ]; then
  # Git root is the hand-off folder: HEAD already carries DeChimexPrints/ (+ tracked
  # AI_HANDOFF.txt), which is exactly the hand-off zip layout.
  git -C "$top" archive --format=tar HEAD | tar -C "$stage" -xf -
else
  # Git root is an ancestor of the hand-off folder: archive only the hand-off
  # subtree and strip its prefix so the zip root carries DeChimexPrints/ +
  # AI_HANDOFF.txt exactly like the other layouts.
  rel="${handoff#"$top"/}"
  depth="$(printf '%s' "$rel" | awk -F/ '{print NF}')"
  git -C "$top" archive --format=tar HEAD "$rel" | tar -C "$stage" --strip-components="$depth" -xf -
fi

# Ship the continuity doc beside the solution folder when it can be found
# (no-op when it is already git-tracked and came from HEAD, as in the v036 layout).
[ -f "$stage/AI_HANDOFF.txt" ] || for cand in "$root/AI_HANDOFF.txt" "$root/../AI_HANDOFF.txt" "$root/../uploads/AI_HANDOFF.txt" "$root/../../uploads/AI_HANDOFF.txt"; do
  if [ -f "$cand" ]; then cp "$cand" "$stage/AI_HANDOFF.txt"; break; fi
done

# Guard both archived settings files, not merely the working tree.
python3 - "$stage" <<'PYGUARD'
import json, pathlib, sys
host = pathlib.Path(sys.argv[1]) / "DeChimexPrints/src/DeChimex.Host"
expected = None
for name in ("appsettings.json", "appsettings.Development.json"):
    data = json.loads((host / name).read_text())
    keys = [data["Brevo"]["ApiKey"], data["Paystack"]["SecretKey"], data["Paystack"]["PublicKey"]]
    for key, prefix, length in zip(keys, ("xkeysib-", "sk_test_", "pk_test_"), (89, 48, 48)):
        if not isinstance(key, str) or not key.startswith(prefix) or len(key) != length:
            sys.exit("REFUSING: required provider key missing or malformed in " + name)
    if expected is not None and keys != expected:
        sys.exit("REFUSING: provider keys differ between settings files")
    expected = keys
print("Verified all three provider keys in both archived settings files (values hidden).")
PYGUARD

rm -f "$out"
(cd "$stage" && zip -qr "$out" .)
echo "Wrote $out"
unzip -l "$out" | tail -1
