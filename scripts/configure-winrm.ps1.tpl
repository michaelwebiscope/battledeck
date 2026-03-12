# Minimal bootstrap: configure WinRM for Ansible (HTTPS on 5986)
# Uses Ansible's official ConfigureRemotingForAnsible.ps1
# -SkipNetworkProfileCheck: required for Azure VMs (often in PUBLIC profile)
# -GlobalHttpFirewallAccess: allow WinRM from any source (NSG restricts at Azure level)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = "Stop"

$url = "https://raw.githubusercontent.com/ansible/ansible-documentation/devel/examples/scripts/ConfigureRemotingForAnsible.ps1"
$file = "$env:TEMP\ConfigureRemotingForAnsible.ps1"

Write-Host "=== Navalansible: Configuring WinRM for Ansible ===" -ForegroundColor Cyan
Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
& powershell.exe -ExecutionPolicy Bypass -File $file -SkipNetworkProfileCheck -GlobalHttpFirewallAccess
Write-Host "=== WinRM configured. Run: ansible-playbook -i inventory.yml playbooks/site.yml ===" -ForegroundColor Green
