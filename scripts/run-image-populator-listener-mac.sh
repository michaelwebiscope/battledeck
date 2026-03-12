#!/usr/bin/env bash
# Run ImagePopulator in listener mode on macOS with optional New Relic Java agent.
# Usage:
#   ./scripts/run-image-populator-listener-mac.sh
#   NEW_RELIC_LICENSE_KEY=your_key ./scripts/run-image-populator-listener-mac.sh
#
# Requires: Java 17+, Maven. Optional: NEW_RELIC_LICENSE_KEY for APM.

set -e
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT/NavalArchive.ImagePopulator"

# Prefer JAVA_HOME; on macOS Homebrew installs OpenJDK but /usr/bin/java is a stub
if [[ -z "$JAVA_HOME" ]]; then
  for candidate in /opt/homebrew/opt/openjdk/bin/java /opt/homebrew/opt/openjdk@17/bin/java; do
    if [[ -x "$candidate" ]] && "$candidate" -version &>/dev/null; then
      export JAVA_HOME="$(dirname "$(dirname "$candidate")")"
      break
    fi
  done
  if [[ -z "$JAVA_HOME" ]] && [[ -x /usr/libexec/java_home ]]; then
    JAVA_HOME="$(/usr/libexec/java_home 2>/dev/null)" || true
  fi
fi
if [[ -n "$JAVA_HOME" ]] && [[ -x "$JAVA_HOME/bin/java" ]]; then
  JAVA_CMD="$JAVA_HOME/bin/java"
else
  JAVA_CMD="java"
fi

# Build JAR
echo "Building ImagePopulator..."
mvn -q package -DskipTests
JAR=$(ls target/image-populator-*.jar 2>/dev/null | head -1)
if [[ -z "$JAR" ]]; then
  echo "No image-populator JAR found in target/" >&2
  exit 1
fi

# Optional New Relic Java agent
NR_DIR=".newrelic-java"
NR_JAR="$NR_DIR/newrelic.jar"
NR_YML="$NR_DIR/newrelic.yml"
JAVA_OPTS=()

# License key: prefer env, else try terraform-navalansible/terraform.tfvars
if [[ -z "$NEW_RELIC_LICENSE_KEY" ]] && [[ -f "$REPO_ROOT/terraform-navalansible/terraform.tfvars" ]]; then
  NEW_RELIC_LICENSE_KEY="$(grep -E '^\s*newrelic_license_key\s*=' "$REPO_ROOT/terraform-navalansible/terraform.tfvars" 2>/dev/null | sed -E 's/^[^=]*=\s*["'\'']?//; s/["'\'']?\s*$//' | tr -d ' ')"
fi

# Sanitize: use only the raw key value (no variable name or quotes) so the collector URL is valid
if [[ -n "$NEW_RELIC_LICENSE_KEY" ]]; then
  RAW_KEY="$NEW_RELIC_LICENSE_KEY"
  NEW_RELIC_LICENSE_KEY="$(echo "$RAW_KEY" | sed -E 's/^[^=]*=\s*["'\'']?//; s/["'\'']?\s*$//' | tr -d ' ')"
  [[ -z "$NEW_RELIC_LICENSE_KEY" ]] && NEW_RELIC_LICENSE_KEY="$RAW_KEY"
fi

if [[ -n "$NEW_RELIC_LICENSE_KEY" ]]; then
  mkdir -p "$NR_DIR"
  if [[ ! -f "$NR_JAR" ]]; then
    echo "Downloading New Relic Java agent..."
    curl -sL -o "$NR_JAR" "https://download.newrelic.com/newrelic/java-agent/newrelic-agent/current/newrelic.jar"
  fi
  # Config: license from env, app name for local dev
  cat > "$NR_YML" <<EOF
common: &default_settings
  license_key: '$NEW_RELIC_LICENSE_KEY'
  app_name: NavalArchiveImagePopulator
  distributed_tracing:
    enabled: true
  log_level: info
  log_file_path: '$REPO_ROOT/NavalArchive.ImagePopulator/$NR_DIR/logs'
production:
  <<: *default_settings
EOF
  JAVA_OPTS+=(-javaagent:"$NR_JAR" -Dnewrelic.config.file="$NR_YML")
  echo "New Relic Java agent enabled (app_name: NavalArchiveImagePopulator)"
else
  echo "Tip: set NEW_RELIC_LICENSE_KEY to enable New Relic APM"
fi

API_BASE="${API_URL:-http://localhost:5000}"
PORT="${IMAGE_POPULATOR_PORT:-5099}"
echo "Starting ImagePopulator listener: API=$API_BASE port=$PORT"
exec "$JAVA_CMD" "${JAVA_OPTS[@]}" -jar "$JAR" "$API_BASE" --listen --port "$PORT"
