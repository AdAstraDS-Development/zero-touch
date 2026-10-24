resource "azurerm_user_assigned_identity" "cmk" {
  name                = "id-${var.project}-${var.environment}"
  location            = azurerm_resource_group.ai.location
  resource_group_name = azurerm_resource_group.ai.name
}