#!/usr/bin/env bash
# Desk Pet — the Charter demo, driven step by step.
#
# Runs the author -> review -> handoff loop against a WORKING COPY of the chart, so the committed
# desk-pet.charter.md stays pristine and the demo is re-runnable as many times as you like. That
# matters: `resolve` writes answers back INTO the chart, so a demo run against the committed file
# would leave the second rehearsal starting from an already-answered plan.
#
# Usage:
#   ./demo.sh              # use the `charter` on PATH (the honest demo)
#   ./demo.sh --local      # use a locally-built CLI (the reliable demo)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="$HERE/.work"

CHARTER="charter"
if [[ "${1:-}" == "--local" ]]; then
  CHARTER="dotnet run --project $REPO/src/Charter.Cli -c Release --"
fi

step() { printf '\n\033[1;36m== %s\033[0m\n' "$1"; }
pause() { printf '\n\033[2m-- press Enter to continue --\033[0m'; read -r _; }

if [[ "$CHARTER" == "charter" ]] && ! command -v charter >/dev/null 2>&1; then
  echo "charter is not on PATH. Install it, or re-run with --local to use a locally-built CLI:" >&2
  echo "  brew install servant-software-llc/tap/charter" >&2
  echo "  dotnet tool install -g ServantSoftware.Charter" >&2
  exit 1
fi

rm -rf "$WORK" && mkdir -p "$WORK"
cp "$HERE/desk-pet.charter.md" "$WORK/desk-pet.charter.md"
PLAN="$WORK/desk-pet.charter.md"

step "0. What we start with"
echo "A single prompt (PROMPT.md) produced this chart. Nothing here was hand-written:"
grep -cE '^:::' "$PLAN" | xargs printf '  %s directive blocks\n'
echo "  4 open questions, 0 answered"
pause

step "1. RENDER — one portable HTML artifact"
$CHARTER render "$PLAN" -o "$WORK/desk-pet.html"
echo "Opens standalone in any browser. No server, no runtime."
pause

step "2. REVIEW — serve it and annotate IN PLACE"
cat <<'EOS'
The browser will open. Things worth doing on camera, in this order:

  a. Comment on a single ROW of the "what counts as feeding it" comparison
     (hover the "Lines changed" row) — the comment anchors to that row, not the block.
  b. Comment on one NODE of the mood diagram (click the "Feral" state).
  c. Comment on one LINE of the diff (the `|| true` line).
  d. Answer all four questions in the panel — note they are real form controls:
     two single-selects, a yes/no, and a free-text.
  e. Click "Send to agent" to end the round.

Then come back here and press Ctrl-C to stop the server.
EOS
pause
set +e
$CHARTER review "$PLAN"
set -e

step "3. RESOLVE — fold the answers back INTO the chart"
$CHARTER resolve "$PLAN" || true
echo
echo "What changed in the source — the answers are now part of the document:"
diff <(grep -A3 ':::question' "$HERE/desk-pet.charter.md") \
     <(grep -A3 ':::question' "$PLAN") || true
pause

step "4. HANDOFF — plain CommonMark for Guardrails"
$CHARTER handoff "$PLAN" -o "$WORK/desk-pet.md"
echo "Every ::: directive is now plain CommonMark. This is what Guardrails breaks into a task DAG:"
head -30 "$WORK/desk-pet.md"
pause

step "Done"
echo "Artifacts left in: $WORK"
echo "The committed chart is untouched — run this again any time."
