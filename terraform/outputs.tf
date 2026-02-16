output "api_url" {
  description = "API base URL"
  value       = var.use_app_service ? "https://${azurerm_windows_web_app.api[0].default_hostname}" : null
}

output "web_url" {
  description = "Web app URL"
  value       = var.use_app_service ? "https://${azurerm_windows_web_app.web[0].default_hostname}" : null
}

output "resource_group" {
  description = "Resource group name"
  value       = local.rg_name
}

output "api_app_name" {
  description = "API App Service name (for deploy script)"
  value       = var.use_app_service ? azurerm_windows_web_app.api[0].name : null
}

output "web_app_name" {
  description = "Web App Service name (for deploy script)"
  value       = var.use_app_service ? azurerm_windows_web_app.web[0].name : null
}
