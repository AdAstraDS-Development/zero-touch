resource "azurerm_key_vault" "agent" {
  name                       = "kv-${var.project}-${var.environment}"
  location                   = var.location
  resource_group_name        = azurerm_resource_group.ai.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
}

# Grant current user Key Vault Administrator role
resource "azurerm_role_assignment" "kv_admin_current_user" {
  principal_id         = data.azurerm_client_config.current.object_id
  scope                = azurerm_key_vault.agent.id
  role_definition_name = "Key Vault Administrator"
}

# Wait for RBAC propagation
resource "time_sleep" "rbac_wait" {
  create_duration = "60s"

  depends_on = [azurerm_role_assignment.kv_admin_current_user]
}

# Key Vault Key for encryption
resource "azurerm_key_vault_key" "cmk" {
  key_opts = [
    "decrypt",
    "encrypt",
    "sign",
    "unwrapKey",
    "verify",
    "wrapKey",
  ]
  key_type     = "RSA"
  key_vault_id = azurerm_key_vault.agent.id
  name         = "cmk-key"
  key_size     = 2048

  depends_on = [time_sleep.rbac_wait]
}

# Allow the hosted agent identities to read the GitHub App private key secret
resource "azurerm_role_assignment" "kv_secrets_user_agents" {
  for_each             = toset(var.key_vault_secrets_readers)
  scope                = azurerm_key_vault.agent.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = each.value
}
