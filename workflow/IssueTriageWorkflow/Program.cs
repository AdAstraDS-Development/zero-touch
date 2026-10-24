// Issue Triage Workflow - a Foundry hosted agent built with Microsoft Agent Framework workflows.
//
// Given a GitHub issue reference, the workflow reads the issue, lets the issue triage agent
// (agents/IssueTriageAgent) inspect the repository's source code, decide whether the issue is
// clear enough to be worked on and estimate a T-shirt size (XS / S / L / XL), posts the result as a comment, sets the triage labels and
// continues on a size-specific branch. See IssueTriageWorkflowFactory.

using Azure.AI.Projects;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using DotNetEnv;
using GitHubTools;
using IssueTriageAgent;
using IssueTriageWorkflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Workflows;

// Local development only; in Foundry the platform injects the environment variables.
Env.TraversePath().Load();

var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");

// azd substitutes an empty string for undefined variables, so skip blanks rather than using ??.
var deploymentName = FirstNonBlank(
    Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
    Environment.GetEnvironmentVariable("FOUNDRY_MODEL"),
    "gpt-4o");

var agentName = FirstNonBlank(Environment.GetEnvironmentVariable("AGENT_NAME"), "issue-triage");

// GitHub App authentication: the app ID (or client ID) and the app's private key.
var gitHubAppId = Environment.GetEnvironmentVariable("GITHUB_APP_ID");
if (string.IsNullOrWhiteSpace(gitHubAppId))
{
    throw new InvalidOperationException("GITHUB_APP_ID is not set.");
}

var credential = new DefaultAzureCredential();
var gitHubAppPrivateKey = await ReadPrivateKeyAsync(credential);

// Optional comma-separated allowlist, e.g. "my-org/my-repo,my-org/*". Empty = every repository
// the GitHub App is installed on.
var allowedRepositories = (Environment.GetEnvironmentVariable("GITHUB_ALLOWED_REPOS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var gitHubApiUrl = new Uri(FirstNonBlank(
    Environment.GetEnvironmentVariable("GITHUB_API_URL"), "https://api.github.com/"));
var gitHubClientFactory = new GitHubAppClientFactory(gitHubAppId, gitHubAppPrivateKey, gitHubApiUrl);
var gitHub = new GitHubIssueService(gitHubClientFactory, allowedRepositories);
var gitHubCode = new GitHubCodeService(gitHubClientFactory, allowedRepositories);
GitHubCodeService.DeleteStaleSnapshots(TimeSpan.FromHours(1));

AIAgent triageAgent = IssueTriageAgentFactory.Create(new AIProjectClient(new Uri(endpoint), credential), deploymentName);

// The workflow is exposed as one agent, so Foundry hosting and invocation stay the same.
AIAgent agent = IssueTriageWorkflowFactory.Build(triageAgent, gitHub, gitHubCode, agentName)
    .AsAIAgent(
        id: agentName,
        name: agentName,
        description: "Checks GitHub issues for clarity, estimates a T-shirt size based on the source code, comments and labels the result on the issue.",
        includeWorkflowOutputsInResponse: true);

// Host the workflow in Foundry using the Responses protocol.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);

var app = builder.Build();
app.MapFoundryResponses();

app.Run();

static string FirstNonBlank(params string?[] candidates) =>
    Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c))!;

// The PEM key is read, in order of precedence, from:
// - a file (GITHUB_APP_PRIVATE_KEY_PATH), for local development,
// - an Azure Key Vault secret (AZURE_KEY_VAULT_URI, secret name GITHUB_APP_PRIVATE_KEY_SECRET_NAME,
//   default "github-app-private-key"), using the agent's Azure identity,
// - GITHUB_APP_PRIVATE_KEY directly.
// Secret and variable values may be PEM text (newlines may be escaped as "\n") or base64-encoded PEM.
static async Task<string> ReadPrivateKeyAsync(Azure.Core.TokenCredential credential)
{
    var path = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY_PATH");
    if (!string.IsNullOrWhiteSpace(path))
    {
        return File.ReadAllText(path);
    }

    var value = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY");
    var keyVaultUri = Environment.GetEnvironmentVariable("AZURE_KEY_VAULT_URI");
    if (!string.IsNullOrWhiteSpace(keyVaultUri))
    {
        var secretName = FirstNonBlank(
            Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY_SECRET_NAME"), "github-app-private-key");
        var secret = await new SecretClient(new Uri(keyVaultUri), credential).GetSecretAsync(secretName);
        value = secret.Value.Value;
    }

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            "Set AZURE_KEY_VAULT_URI, GITHUB_APP_PRIVATE_KEY or GITHUB_APP_PRIVATE_KEY_PATH.");
    }

    return value.Contains("-----BEGIN", StringComparison.Ordinal)
        ? value.Replace("\\n", "\n")
        : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
}
