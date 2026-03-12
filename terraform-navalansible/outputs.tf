output "vm_name" {
  description = "VM name (for ansible inventory)"
  value       = azurerm_windows_virtual_machine.main.name
}

output "vm_public_ip" {
  description = "VM public IP (Ansible connects via WinRM HTTPS on 5986)"
  value       = azurerm_public_ip.main.ip_address
}

output "resource_group" {
  description = "Resource group name"
  value       = local.rg_name
}

output "ansible_inventory" {
  description = "Example ansible-playbook command"
  value       = "ansible-playbook -i ansible/inventory.yml ansible/playbooks/site.yml -e 'ansible_host=${azurerm_public_ip.main.ip_address}'"
}
