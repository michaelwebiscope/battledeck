#!/bin/bash
# Dynadog Destroyer - Removes Dynatrace and Datadog from Linux
# Run as root: sudo ./dynadog-destroyer.sh
#
# Dynatrace: /opt/dynatrace, /var/lib/dynatrace, /var/log/dynatrace, /etc/ld.so.preload
# Datadog:   /opt/datadog-agent, /etc/datadog-agent, /var/log/datadog, package: datadog-agent

set -e
RED='\033[0;31m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "\n${RED}=== DYNADOG DESTROYER ===${NC}"
echo -e "${YELLOW}Removing Dynatrace OneAgent and Datadog Agent from this system.\n${NC}"

# ========== DYNATRACE ==========
echo -e "${CYAN}--- Dynatrace OneAgent ---${NC}"

# Stop OneAgent (if running)
if command -v /opt/dynatrace/oneagent/agent/bin/oneagentwrapper &>/dev/null; then
    /opt/dynatrace/oneagent/agent/bin/oneagentwrapper --set-config enable=false 2>/dev/null || true
fi

# Remove ld.so.preload injection
if [ -f /etc/ld.so.preload ]; then
    sed -i.bak '/dynatrace/d' /etc/ld.so.preload 2>/dev/null || true
    [ -s /etc/ld.so.preload ] || rm -f /etc/ld.so.preload
    echo -e "  ${GREEN}Cleaned /etc/ld.so.preload${NC}"
fi

# Remove install dirs
DYNATRACE_PATHS=(
    "/opt/dynatrace"
    "/var/lib/dynatrace"
    "/var/log/dynatrace"
)
for p in "${DYNATRACE_PATHS[@]}"; do
    if [ -d "$p" ]; then
        rm -rf "$p"
        echo -e "  ${GREEN}Removed: $p${NC}"
    fi
done

# ========== DATADOG ==========
echo -e "\n${CYAN}--- Datadog Agent ---${NC}"

# Stop service
systemctl stop datadog-agent 2>/dev/null || service datadog-agent stop 2>/dev/null || true

# Package uninstall
if command -v apt-get &>/dev/null; then
    apt-get remove -y datadog-agent 2>/dev/null || true
    apt-get purge -y datadog-agent 2>/dev/null || true
    echo -e "  ${GREEN}Removed datadog-agent package (apt)${NC}"
elif command -v yum &>/dev/null; then
    yum remove -y datadog-agent 2>/dev/null || true
    echo -e "  ${GREEN}Removed datadog-agent package (yum)${NC}"
elif command -v zypper &>/dev/null; then
    zypper remove -y datadog-agent 2>/dev/null || true
    echo -e "  ${GREEN}Removed datadog-agent package (zypper)${NC}"
fi

# Remove install dirs
DATADOG_PATHS=(
    "/opt/datadog-agent"
    "/etc/datadog-agent"
    "/var/log/datadog"
)
for p in "${DATADOG_PATHS[@]}"; do
    if [ -d "$p" ]; then
        rm -rf "$p"
        echo -e "  ${GREEN}Removed: $p${NC}"
    fi
done

# Remove dd-agent user
userdel dd-agent 2>/dev/null || true
echo -e "  ${GREEN}Removed dd-agent user (if existed)${NC}"

# Disable systemd service
systemctl disable datadog-agent 2>/dev/null || true
rm -f /etc/systemd/system/datadog-agent.service 2>/dev/null || true
rm -f /lib/systemd/system/datadog-agent.service 2>/dev/null || true
systemctl daemon-reload 2>/dev/null || true

echo -e "\n${RED}=== DYNADOG DESTROYER COMPLETE ===${NC}"
echo -e "${YELLOW}Reboot recommended to clear any loaded libraries.\n${NC}"
