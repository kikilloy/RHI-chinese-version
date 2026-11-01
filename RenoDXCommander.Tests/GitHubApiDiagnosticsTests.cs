using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// "Verification skipped" is only useful if it says why. These are the GitHub API failure shapes
/// seen in practice: a revoked token looks exactly like a rate limit (403) until you read the body.
/// </summary>
public class GitHubApiDiagnosticsTests
{
    private const string BadCredentials = """
    { "message": "Bad credentials", "documentation_url": "https://docs.github.com/rest" }
    """;

    private const string RateLimited = """
    { "message": "API rate limit exceeded for 1.2.3.4.", "documentation_url": "https://docs.github.com/rest" }
    """;

    [Fact]
    public void BadCredentials_PointsAtTheToken()
    {
        var text = GitHubApiDiagnostics.Explain(403, BadCredentials);
        Assert.Contains("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accounts & Data", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unauthorized_IsAlwaysATokenProblem()
    {
        Assert.Contains("token", GitHubApiDiagnostics.Explain(401, null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RateLimit_ExplainsTheLimit()
    {
        var text = GitHubApiDiagnostics.Explain(403, RateLimited);
        Assert.Contains("rate limit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5000", text);
    }

    [Fact]
    public void RateLimit_DetectedOnStatusCodeToo()
    {
        Assert.Contains("rate limit", GitHubApiDiagnostics.Explain(429, null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForbiddenWithoutExplanation_StaysHonest()
    {
        var text = GitHubApiDiagnostics.Explain(403, null);
        Assert.Contains("refused", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotFound_IsReportedAsMissing()
    {
        Assert.Contains("not found", GitHubApiDiagnostics.Explain(404, null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerError_MentionsGithub()
    {
        Assert.Contains("GitHub", GitHubApiDiagnostics.Explain(502, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("<html>gateway</html>")]
    public void ExtractMessage_NeverThrows(string? body)
    {
        _ = GitHubApiDiagnostics.ExtractMessage(body);
    }

    [Fact]
    public void ExtractMessage_ReadsTheField()
    {
        Assert.Equal("Bad credentials", GitHubApiDiagnostics.ExtractMessage(BadCredentials));
    }

    [Fact]
    public void BriefForm_IsShort()
    {
        Assert.Equal("GitHub token invalid", GitHubApiDiagnostics.ExplainBrief(403, BadCredentials));
        Assert.Equal("GitHub rate limit", GitHubApiDiagnostics.ExplainBrief(403, RateLimited));
        Assert.Equal("GitHub API HTTP 404", GitHubApiDiagnostics.ExplainBrief(404, null));
    }

    [Fact]
    public void IntegritySkipReason_IsRecordedAndCleared()
    {
        MirrorRuntime.Reset();
        Assert.Null(MirrorRuntime.IntegritySkipReason);

        MirrorRuntime.ReportIntegritySkip("GitHub token invalid");
        Assert.Equal("GitHub token invalid", MirrorRuntime.IntegritySkipReason);

        MirrorRuntime.ClearIntegritySkip();
        Assert.Null(MirrorRuntime.IntegritySkipReason);

        MirrorRuntime.Reset();
    }
}
