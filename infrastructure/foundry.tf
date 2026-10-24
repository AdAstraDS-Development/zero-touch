resource "azurerm_resource_group" "ai" {
  name     = "rg-${var.project}-${var.environment}"
  location = var.location
}

module "ai_foundry" {
  source  = "Azure/avm-ptn-aiml-ai-foundry/azurerm"
  version = "0.11.3"

  base_name                  = "zt${var.environment}"
  location                   = var.location
  resource_group_resource_id = azurerm_resource_group.ai.id

  enable_telemetry         = false
  create_byor              = false
  create_private_endpoints = false

  resource_names = {
    ai_foundry = "aif-${var.project}-${var.environment}"
  }

  ai_foundry = {
    public_network_access_enabled = true
    customer_managed_key = {
      key_vault_resource_id              = azurerm_key_vault.agent.id
      key_name                           = azurerm_key_vault_key.cmk.name
      key_version                        = null # Use latest version
      user_assigned_identity_resource_id = azurerm_user_assigned_identity.cmk.id
    }
  }

  ai_model_deployments = {
    gpt56_luna = {
      name = "gpt-5.6-luna"
      model = {
        format  = "OpenAI"
        name    = "gpt-5.6-luna"
        version = "2026-07-09"
      }
      scale = {
        type     = "GlobalStandard"
        capacity = var.ai_capacity
      }
    }
  }

  ai_projects = {
    zero_touch_vdv = {
      name         = "aifp-${var.project}-vdv-${var.environment}"
      display_name = "Zero Touch VDV Automation"
      description  = "AI Foundry project for Zero Touch VDV Automation workloads."
    }
  }
}

# Grant project-management access on the VDV Automation project.
resource "azurerm_role_assignment" "foundry_project_managers" {
  for_each             = toset(var.ai_foundry_project_managers)
  scope                = module.ai_foundry.ai_foundry_project_id["zero_touch_vdv"]
  role_definition_name = "Foundry Project Manager"
  principal_id         = each.value
}

# Allow callers (e.g. the GitHub Actions identity of the issue triage trigger) to invoke the
# hosted agents in the VDV Automation project.
resource "azurerm_role_assignment" "foundry_agent_invokers" {
  for_each             = toset(var.agent_invoker_principal_ids)
  scope                = module.ai_foundry.ai_foundry_project_id["zero_touch_vdv"]
  role_definition_name = "Foundry User"
  principal_id         = each.value
}
