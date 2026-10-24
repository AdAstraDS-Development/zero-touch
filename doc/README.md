# Issue Triage Workflow

A Foundry **hosted agent**, built in C# as a Microsoft Agent Framework **workflow**. Given a GitHub issue, it:

1. reads the issue (title, body, labels, comments),
2. lets the issue triage agent check whether the issue is **clear** enough to work on, reading the **source code** of the repository (default branch) to locate the affected code,
3. if it is clear, lets the agent estimate a **T-shirt size** based on the code that has to change: `XS` (very small), `S` (small), `L` (large) or `XL` (very large),
4. posts the result as a **comment on the issue**. If the issue is not clear, the comment lists concrete questions for the author,
5. sets the labels `triage/ready` and `size/<size>` when the issue is clear, or `triage/needs-info` when it is not,
6. continues on a size-specific branch (`XS`/`S` or `L`/`XL`). These branches are placeholders for the follow-up steps.

When the workflow triages the same issue again (for example after the author answers the questions), it updates its previous comment instead of adding a new one. It also replaces an outdated `size/*` label, and swaps `triage/ready` and `size/*` for `triage/needs-info` when the issue is no longer clear (and the other way round once it is).

Triggered from GitHub (see [Trigger from GitHub](#trigger-from-github)), an issue stays in the triage step until it is clear: every new comment or edit triages it again, and once it has `triage/ready` the workflow continues to the size-specific branch and further comments are ignored.

## Repository layout

| Folder | Content |
|--------|---------|
| `agents/` | Agent definitions, one subfolder (and C# class library) per agent: instructions, output schema and a factory. Agents have no hosting and no side effects, so several workflows can reuse them. |
| `workflow/` | Workflows, one subfolder (and C# project) per workflow. A workflow combines agents from `agents/` and tools from `tools/`, and is the unit that is hosted and deployed to Foundry. |
| `tools/` | Reusable integrations (for example GitHub) used by workflows, one subfolder (and C# project) per tool. |
| `infrastructure/` | Terraform code. |

All C# projects are part of `azure-zero-touch.slnx`, so everything builds at once with `dotnet build azure-zero-touch.slnx`. Common build settings live in `Directory.Build.props`, and package versions are managed centrally in `Directory.Packages.props`. To add an agent, workflow or tool, create its subfolder and project and add it to the solution (for example `dotnet sln add --solution-folder workflow workflow/<Name>/<Name>.csproj`). Workflows reference agents and tools with a `ProjectReference`. Each workflow has its own `azure.yaml` and is deployed as its own hosted agent.

## How it works

| File | Purpose |
|------|---------|
```
parse-reference → fetch-issue → triage (agent) → publish-triage → switch:
                                                                    not clear → not-clear
                                                                    XS, S     → small-issue
                                                                    L, XL     → large-issue
```

| File | Purpose |
|------|---------|
| `agents/IssueTriageAgent/IssueTriageAgentFactory.cs` | Builds the triage agent (`AIProjectClient.AsAIAgent`) with the instructions (clarity criteria, size scale, how to use the source code). `AssessIssueAsync` runs it with structured output and the read-only source code tools passed by the caller, and validates the result, asking the model once to correct an invalid result. |
| `agents/IssueTriageAgent/TriageAssessment.cs` | The agent's output schema (`IsClear`, `Size`, `Rationale`, `MissingInformation`) and the allowed sizes `XS/S/L/XL`. |
| `workflow/IssueTriageWorkflow/IssueTriageWorkflowFactory.cs` | Wires the executors in `Executors/` into the workflow above (`WorkflowBuilder`, `AddSwitch`). |
| `workflow/IssueTriageWorkflow/Program.cs` | Reads the configuration, creates the agent and the workflow, exposes the workflow as an agent (`AsAIAgent`) and hosts it with the Responses protocol (`AddFoundryResponses` / `MapFoundryResponses`). |
| `workflow/IssueTriageWorkflow/azure.yaml` | `azd` definition: ZIP deploy to Foundry with the `dotnet_10` runtime. Because the workflow references projects under `agents/` and `tools/`, Foundry cannot build it from the workflow folder alone; `azd deploy` publishes it locally and uploads the output with `dependencyResolution: bundled`. |
| `tools/GitHubTools/GitHubIssueService.cs` | Built on [Octokit](https://github.com/octokit/octokit.net). Reads the issue, posts or updates the triage comment, and syncs the `triage/ready`, `triage/needs-info` and `size/*` labels. The comment is rendered in code, so the format is always the same. |
| `tools/GitHubTools/GitHubCodeService.cs` | Pins the head commit of the default branch (in `fetch-issue`) and downloads the code at that commit as one tarball (in `triage`) into a temporary directory. Snapshots left behind by an aborted run are deleted at startup. |
| `tools/GitHubTools/RepositorySnapshot.cs` | The extracted code and the read-only operations on it: file list, file content (with line numbers, paged) and a case-insensitive text search returning the matching lines. `TriageExecutor` exposes them to the agent as the tools `list_files`, `read_file` and `search_code` (at most 25 calls per triage, all local), and deletes the snapshot when the assessment is done. Only regular files are extracted (no symbolic or hard links); files over 1 MB are skipped and extraction stops at 300 MB or 20,000 files, which the tools then mention to the agent. |
| `tools/GitHubTools/IssueReference.cs` | Parses `owner/repo#n` and issue URLs from the request. |
| `tools/GitHubTools/GitHubAppClientFactory.cs` | GitHub App authentication. Signs a JWT with the app's private key, looks up the app's installation on the repository, and exchanges the JWT for an installation token. Clients are cached per repository until shortly before the token expires. |

The issue content (and the repository's source code) is untrusted input. It is kept away from write access in four ways:

- the model only has read-only tools over a snapshot of the issue's repository in a temporary directory (paths cannot leave it, links are not extracted), and only returns an assessment;
- the issue reference is taken from the request in code, so the model cannot redirect the workflow to another issue;
- the comment and labels are produced in code from the validated assessment;
- the workflow can only reach repositories where the GitHub App is installed (`GITHUB_ALLOWED_REPOS` can narrow this further).

Comments and labels appear as set by `<your-app>[bot]`.

## Prerequisites

- .NET 10 SDK
- A Foundry project with a model deployment (for example `gpt-4o`)
- Azure CLI (`az login`) and Azure Developer CLI with the agents extension: `azd extension install azure.ai.agents`
- A **GitHub App** (see below)

## Set up the GitHub App

1. Create an app under **GitHub → Settings → Developer settings → GitHub Apps → New GitHub App** (or in your organization's settings).
   - Homepage URL: any URL. Webhook: turn off **Active**. Issue events reach the workflow through GitHub Actions (see [Trigger from GitHub](#trigger-from-github)).
   - Repository permissions: **Issues: Read and write** (comments and labels) and **Contents: Read-only** (source code for the assessment). **Metadata: Read-only** is selected automatically.
   - When adding **Contents** to an existing app, accept the new permission on each installation (GitHub asks the organization or repository owner).
2. Note the **App ID** and generate a **private key**, which downloads a `.pem` file.
3. **Install** the app on the organization or on the repositories to triage.

## Run locally

For GitHub Enterprise Cloud on `oebb.ghe.com`, set `GITHUB_API_URL=https://api.oebb.ghe.com/`
in `.env`. Both app authentication and issue operations use this API root. The default is
`https://api.github.com/`. See [GitHub's Enterprise Cloud API documentation](https://docs.github.com/en/enterprise-cloud%40latest/rest/apps/apps).

```bash
cp .env.example .env   # fill in FOUNDRY_PROJECT_ENDPOINT, GITHUB_APP_ID, GITHUB_APP_PRIVATE_KEY_PATH, ...
az login
dotnet run --project workflow/IssueTriageWorkflow/IssueTriageWorkflow.csproj
```

In a second terminal:

```bash
curl -s http://localhost:8088/responses -H 'Content-Type: application/json' \
  -d '{"model":"issue-triage","input":"Triage https://github.com/<owner>/<repo>/issues/<number>"}'
```

## Deploy to Foundry

Run these steps from an empty working directory outside the repo:

```bash
azd auth login
azd ai agent init -m <path-to>/zero-touch/workflow/IssueTriageWorkflow/azure.yaml -d <model-deployment>
cd issue-triage
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <model-deployment>
azd env set GITHUB_APP_ID <app-id>
az keyvault secret set --vault-name <vault-name> --name github-app-private-key \
  --file <path-to>/github-app.private-key.pem
azd env set AZURE_KEY_VAULT_URI "https://<vault-name>.vault.azure.net/"
azd env set GITHUB_ALLOWED_REPOS "<owner>/<repo>"
azd provision
azd deploy
azd ai agent invoke "Triage <owner>/<repo>#<number>"
```

The agent reads the private key from the Key Vault secret at startup using its Azure identity
(`DefaultAzureCredential`), so that identity needs the `Key Vault Secrets User` role on the vault.
Use `GITHUB_APP_PRIVATE_KEY_SECRET_NAME` if the secret is not named `github-app-private-key`.
The secret value may be the PEM text or the base64-encoded `.pem` file.

To remove the agent, run `azd down`. Note that `azd down` does not delete the hosted agent itself; delete it explicitly:

```bash
az rest --method delete --url "<project-endpoint>/agents/issue-triage" \
  --url-parameters api-version=v1 force=true --resource https://ai.azure.com
```

## Deploy from GitHub Actions

The workflow at `.github/workflows/deploy-agent.yml` deploys the workflow from `workflow/IssueTriageWorkflow`. It can also be started manually from the Actions tab.

Configure an Azure federated credential for the repository (environment `sandbox`) and set these repository or `sandbox` environment variables:

- `AZURE_CLIENT_ID`, `ARM_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`
- `FOUNDRY_PROJECT_ENDPOINT`, `AZURE_AI_PROJECT_ID`, `FOUNDRY_MODEL_DEPLOYMENT_NAME`
- `GHE_APP_ID`, `GHE_API_URL` (for GitHub.com use `https://api.github.com/`)
- `AZURE_KEY_VAULT_NAME`, `GHE_APP_PRIVATE_KEY_SECRET_NAME`

Store the GitHub App private key as a Key Vault secret. The workflow passes the vault URI and secret name to the agent; the agent's identity in Foundry needs the `Key Vault Secrets User` role on that vault to read the secret at startup.

## Trigger from GitHub

The reusable workflow `.github/workflows/issue-triage.yml` calls the deployed agent for one issue. It signs in to Azure with OIDC, gets a token for `https://ai.azure.com` and posts `Triage <issue-url>` to the agent's Responses endpoint (`<project-endpoint>/agents/issue-triage/endpoint/protocols/openai/responses`). The agent's output appears in the job summary. It can also be started manually from the Actions tab of this repository.

Each repository to triage gets a small caller workflow: copy [`examples/issue-triage-trigger.yml`](examples/issue-triage-trigger.yml) to `.github/workflows/issue-triage.yml` there and replace `<org>`. It runs on:

- `issues`: `opened`, `reopened`, `edited` (the author may add the missing details to the description),
- `issue_comment`: `created`,

and skips pull requests, closed issues, issues that already have `triage/ready`, and comments by bots. The bot filter matters: comments written by the GitHub App (unlike those written with `GITHUB_TOKEN`) trigger workflows, so without it the triage comment would trigger the next run.

Label lifecycle:

```
opened ─► triage ─► unclear ─► comment with questions, triage/needs-info ─┐
                │                                                         │ author comments / edits
                │◄────────────────────────────────────────────────────────┘
                └─► clear ─► triage/ready + size/<size> ─► small-issue / large-issue (no more triggers)
```

No workflow state is kept between runs: each run reads the whole issue again (including the author's answers) and GitHub's labels decide whether to run at all. To triage a ready issue again, remove `triage/ready` and comment.

Setup for a repository to triage:

1. Install the GitHub App on the repository (and add it to `GITHUB_ALLOWED_REPOS` if set).
2. Set the variables `FOUNDRY_PROJECT_ENDPOINT`, `AZURE_CLIENT_ID`, `ARM_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` in the repository or its organization.
3. Add a federated credential to that Azure identity for the repository. For a reusable workflow, the OIDC subject is the **caller** repository, for example `repo:<org>/<repo>:ref:refs/heads/main` for issue events (they run on the default branch). To cover many repositories with one credential, use a flexible federated identity credential or a custom subject claim that includes `job_workflow_ref`.
4. Grant the identity the `Foundry User` role on the Foundry project: add its object ID to `agent_invoker_principal_ids` in `infrastructure/environments/<env>.tfvars`.
5. Allow other repositories to use this repository's workflows: **Settings → Actions → General → Access**.

## Customizing

- **Size scale / clarity criteria / use of the source code:** edit `Instructions` in `agents/IssueTriageAgent/IssueTriageAgentFactory.cs`. If you add a size (for example `M`), also add it to `AllowedSizes` in `agents/IssueTriageAgent/TriageAssessment.cs` and to the switch in `workflow/IssueTriageWorkflow/IssueTriageWorkflowFactory.cs`.
- **Comment format:** edit `RenderComment` in `tools/GitHubTools/GitHubIssueService.cs`.
- **Labels:** `ReadyLabel`, `NeedsInfoLabel` and `SizeLabelPrefix` in `tools/GitHubTools/GitHubIssueService.cs`. GitHub creates missing labels with a default color; create them in the repository beforehand to choose colors.
- **Steps per size:** implement them in `SmallIssueExecutor` / `LargeIssueExecutor` in `workflow/IssueTriageWorkflow/Executors/`, or replace those executors with new ones (or agents from `agents/`) in `IssueTriageWorkflowFactory`. Every executor must be stateless and created with `declareCrossRunShareable: true`, because the hosted workflow serves parallel requests.

## Troubleshooting

If Azure returns `400 invalid_payload` after a tool call with a reasoning model, check the
resolved `Microsoft.Extensions.AI.OpenAI` version. This project pins `10.9.0` because the
transitive `10.6.0` adapter dropped reasoning item IDs on continuation requests. Foundry
hosting uses `store=false`, so these IDs must survive replay alongside encrypted reasoning.
After restoring updated dependencies, stop and restart `dotnet run`.
