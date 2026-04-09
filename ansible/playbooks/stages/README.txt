Staged deploy (default in scripts/deploy-navalansible.sh)
============================================================
Each file runs as a separate ansible-playbook process, so WinRM opens a
fresh connection per stage. Long single-session runs often hang on Azure
Windows VMs; staging avoids one hour-long PyWinRM pipe.

Order matches roles/navalansible/tasks/main.yml.

To run only one stage (recovery): from ansible/
  ansible-playbook playbooks/stages/05-build.yml -e ansible_host=IP -e ...

GitHub Actions: runners must reach the VM on TCP 5986. If NSG allows only
your home IP, either widen the rule (e.g. AzureCloud / temporary) or use a
self-hosted runner that can reach the VM.
