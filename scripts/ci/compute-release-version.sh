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

has_commits() {
  local log_range="$1"
  [[ "$(git rev-list --count "$log_range")" != "0" ]]
}

write_commit_list() {
  local log_range="$1"

  if has_commits "$log_range"; then
    git log --format='- %s (%h)' --reverse "$log_range"
  else
    echo "- No user-facing changes."
  fi
}

write_release_section() {
  local section_tag="$1"
  local log_range="$2"
  local previous_tag="$3"

  echo "## $section_tag"
  echo
  if [[ -n "$previous_tag" ]]; then
    echo "Changes since $previous_tag:"
  else
    echo "Initial release."
  fi
  echo
  write_commit_list "$log_range"
}

notes_file="${RELEASE_NOTES_FILE:-}"
if [[ -n "$notes_file" ]]; then
  write_release_section "$tag" "$range" "$latest_tag" >"$notes_file"
fi

changelog_file="${CHANGELOG_FILE:-}"
if [[ -n "$changelog_file" ]]; then
  historical_tags=()
  while IFS= read -r historical_tag; do
    historical_tags+=("$historical_tag")
  done < <(git tag --list 'v[0-9]*' --sort=v:refname)

  {
    echo "# Changelog"
    echo

    sections_written=0
    if [[ "$released" == "true" ]]; then
      write_release_section "$tag" "$range" "$latest_tag"
      sections_written=$((sections_written + 1))
    fi

    for ((i=${#historical_tags[@]} - 1; i >= 0; i--)); do
      current_tag="${historical_tags[$i]}"
      if [[ "$released" == "true" && "$current_tag" == "$tag" ]]; then
        continue
      fi

      previous_tag=""
      historical_range="$current_tag"
      if ((i > 0)); then
        previous_tag="${historical_tags[$((i - 1))]}"
        historical_range="$previous_tag..$current_tag"
      fi

      if ((sections_written > 0)); then
        echo
      fi
      write_release_section "$current_tag" "$historical_range" "$previous_tag"
      sections_written=$((sections_written + 1))
    done

    if ((sections_written == 0)); then
      echo "No releases yet."
    fi
  } >"$changelog_file"
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
