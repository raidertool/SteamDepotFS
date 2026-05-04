#!/usr/bin/env bash
set -euo pipefail

latest_tag="$(git describe --tags --match 'v[0-9]*' --abbrev=0 2>/dev/null || true)"

if [[ -n "$latest_tag" ]]; then
  base_version="${latest_tag#v}"
  range="$latest_tag..HEAD"
else
  base_version="0.0.0"
  range="HEAD"
fi

IFS=. read -r major minor patch <<<"$base_version"
major="${major:-0}"
minor="${minor:-0}"
patch="${patch:-0}"

bump="none"

if [[ "$(git rev-list --count "$range")" != "0" ]]; then
  if git log --format='%s%n%b' "$range" | grep -Eq '^[a-zA-Z]+(\([^)]+\))?!:|^BREAKING[ -]CHANGE:'; then
    bump="major"
  elif git log --format='%s' "$range" | grep -Eq '^feat(\([^)]+\))?:'; then
    bump="minor"
  elif git log --format='%s' "$range" | grep -Eq '^(fix|perf|refactor|revert)(\([^)]+\))?:'; then
    bump="patch"
  fi
fi

if [[ "$bump" == "none" ]]; then
  released=false
  version="$base_version"
else
  released=true
  case "$bump" in
    major)
      major=$((major + 1))
      minor=0
      patch=0
      ;;
    minor)
      minor=$((minor + 1))
      patch=0
      ;;
    patch)
      patch=$((patch + 1))
      ;;
  esac
  version="$major.$minor.$patch"
fi

tag="v$version"

notes_file="${RELEASE_NOTES_FILE:-}"
if [[ -n "$notes_file" ]]; then
  {
    echo "## $tag"
    echo
    if [[ -n "$latest_tag" ]]; then
      echo "Changes since $latest_tag:"
    else
      echo "Initial release."
    fi
    echo

    if [[ -n "$(git log --format='%s' "$range")" ]]; then
      git log --format='- %s (%h)' --reverse "$range"
    else
      echo "- No user-facing changes."
    fi
  } >"$notes_file"
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "released=$released"
    echo "version=$version"
    echo "tag=$tag"
    echo "previous_tag=$latest_tag"
    echo "bump=$bump"
  } >>"$GITHUB_OUTPUT"
else
  printf 'released=%s\nversion=%s\ntag=%s\nprevious_tag=%s\nbump=%s\n' \
    "$released" "$version" "$tag" "$latest_tag" "$bump"
fi
