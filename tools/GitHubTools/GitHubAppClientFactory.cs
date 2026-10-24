using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octokit;

namespace GitHubTools;

/// <summary>
/// Creates Octokit clients authenticated as a GitHub App installation.
/// Flow: sign a short-lived JWT with the app's private key, look up the app's installation on
/// the repository, exchange the JWT for an installation access token (valid for one hour).
/// Clients are cached per repository until shortly before their token expires.
/// </summary>
public sealed class GitHubAppClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("zero-touch-issue-triage");
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly string _appId;
    private readonly RSA _privateKey;
    private readonly Uri _apiBaseAddress;
    private readonly Dictionary<string, (GitHubClient Client, string Token, DateTimeOffset ExpiresAt)> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="appId">The GitHub App ID (or client ID).</param>
    /// <param name="privateKeyPem">The app's private key in PEM format (PKCS#1 or PKCS#8).</param>
    /// <param name="apiBaseAddress">The API root for GitHub.com or a GitHub Enterprise instance.</param>
    public GitHubAppClientFactory(string appId, string privateKeyPem, Uri? apiBaseAddress = null)
    {
        _appId = appId;
        _apiBaseAddress = apiBaseAddress ?? new Uri("https://api.github.com/");
        if (!_apiBaseAddress.IsAbsoluteUri || _apiBaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("GITHUB_API_URL must be an absolute HTTPS URL.", nameof(apiBaseAddress));
        }
        _privateKey = RSA.Create();
        _privateKey.ImportFromPem(privateKeyPem);
    }

    /// <summary>The API root, e.g. https://api.github.com/.</summary>
    public Uri ApiBaseAddress => _apiBaseAddress;

    public async Task<GitHubClient> GetClientForRepositoryAsync(string owner, string repo) =>
        (await GetInstallationAsync(owner, repo)).Client;

    /// <summary>The installation access token for the repository, for requests made without Octokit.</summary>
    public async Task<string> GetInstallationTokenAsync(string owner, string repo) =>
        (await GetInstallationAsync(owner, repo)).Token;

    private async Task<(GitHubClient Client, string Token)> GetInstallationAsync(string owner, string repo)
    {
        var key = $"{owner}/{repo}";

        await _lock.WaitAsync();
        try
        {
            if (_clients.TryGetValue(key, out var cached) && cached.ExpiresAt - TokenRefreshMargin > DateTimeOffset.UtcNow)
            {
                return (cached.Client, cached.Token);
            }

            var appClient = new GitHubClient(ProductHeader, _apiBaseAddress)
            {
                Credentials = new Credentials(CreateJwt(), AuthenticationType.Bearer),
            };

            var installation = await appClient.GitHubApps.GetRepositoryInstallationForCurrent(owner, repo);
            var token = await appClient.GitHubApps.CreateInstallationToken(installation.Id);

            var client = new GitHubClient(ProductHeader, _apiBaseAddress) { Credentials = new Credentials(token.Token) };
            _clients[key] = (client, token.Token, token.ExpiresAt);
            return (client, token.Token);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string CreateJwt()
    {
        var now = DateTimeOffset.UtcNow;

        // GitHub accepts the numeric app ID or the client ID as issuer.
        object issuer = long.TryParse(_appId, out var numericId) ? numericId : _appId;

        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(), // allow for clock drift
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),   // GitHub allows at most 10 minutes
            iss = issuer,
        }));

        var signature = _privateKey.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
    }
}
