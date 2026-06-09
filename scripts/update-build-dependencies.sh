#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BMR_PATH="$ROOT/third_party/BossmodReborn"
ECOMMONS_PATH="$ROOT/third_party/ECommons"
BMR_REPO="FFXIV-CombatReborn/BossmodReborn"
BMR_URL="https://github.com/$BMR_REPO.git"
ECOMMONS_BRANCH="master"

fail() {
  echo "error: $*" >&2
  exit 1
}

run() {
  printf '+'
  printf ' %q' "$@"
  printf '\n'
  "$@"
}

ensure_clean_submodule() {
  local path="$1"
  local name="$2"

  [[ -d "$path/.git" || -f "$path/.git" ]] || fail "$name was not found at '$path'. Run git submodule update --init --recursive."

  if [[ -n "$(git -C "$path" status --porcelain)" ]]; then
    fail "$name has local changes. Commit, stash, or discard them before updating the dependency pointer."
  fi
}

latest_bossmod_release_tag() {
  if command -v gh >/dev/null 2>&1; then
    local tag
    if tag="$(gh release view --repo "$BMR_REPO" --json tagName --jq .tagName 2>/dev/null)" && [[ -n "$tag" ]]; then
      printf '%s\n' "$tag"
      return
    fi
  fi

  local python_bin=""
  if command -v python3 >/dev/null 2>&1; then
    python_bin="python3"
  elif command -v python >/dev/null 2>&1; then
    python_bin="python"
  fi

  if command -v curl >/dev/null 2>&1 && [[ -n "$python_bin" ]]; then
    local -a curl_args=(-fsSL)
    if [[ -n "${GITHUB_TOKEN:-}" ]]; then
      curl_args+=(-H "Authorization: Bearer $GITHUB_TOKEN")
    fi

    local tag
    if tag="$(
      curl "${curl_args[@]}" "https://api.github.com/repos/$BMR_REPO/releases/latest" \
        | "$python_bin" -c 'import json, sys; print(json.load(sys.stdin).get("tag_name", ""))' 2>/dev/null
    )" && [[ -n "$tag" ]]; then
      printf '%s\n' "$tag"
      return
    fi
  fi

  git ls-remote --tags --refs "$BMR_URL" 'refs/tags/[0-9]*' \
    | sed 's#.*refs/tags/##' \
    | sort -V \
    | tail -n 1
}

cd "$ROOT"

run git submodule update --init --recursive third_party/BossmodReborn third_party/ECommons
ensure_clean_submodule "$BMR_PATH" "BossMod Reborn"
ensure_clean_submodule "$ECOMMONS_PATH" "ECommons"

bmr_tag="$(latest_bossmod_release_tag)"
[[ -n "$bmr_tag" ]] || fail "could not resolve the latest BossMod Reborn release tag."

echo "BossMod Reborn latest release: $bmr_tag"
run git -C "$BMR_PATH" fetch --tags origin
bmr_commit="$(git -C "$BMR_PATH" rev-list -n 1 "$bmr_tag")"
[[ -n "$bmr_commit" ]] || fail "could not resolve BossMod Reborn tag '$bmr_tag'."
run git -C "$BMR_PATH" checkout --detach "$bmr_commit"
run git -C "$BMR_PATH" submodule update --init --recursive

echo "ECommons latest branch: origin/$ECOMMONS_BRANCH"
run git -C "$ECOMMONS_PATH" fetch origin "$ECOMMONS_BRANCH"
ecommons_commit="$(git -C "$ECOMMONS_PATH" rev-parse FETCH_HEAD)"
[[ -n "$ecommons_commit" ]] || fail "could not resolve ECommons branch '$ECOMMONS_BRANCH'."
run git -C "$ECOMMONS_PATH" checkout --detach "$ecommons_commit"

echo
git diff --submodule=short -- third_party/BossmodReborn third_party/ECommons
