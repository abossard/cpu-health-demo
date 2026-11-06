using StressManager.Calculations;

namespace StressManager.Tests;

public class AccessGateTests
{
    private const string Code = "4242";

    [Theory]
    [InlineData("4242", true)]
    [InlineData("4243", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(" 4242", false)]
    public void CodeMatches(string? input, bool expected) => Assert.Equal(expected, AccessGate.CodeMatches(input, Code));

    [Theory]
    [InlineData(Code, true)]
    [InlineData("4243", false)]
    public void TokenMatches_OnlyForTokenOfSameCode(string tokenCode, bool expected) =>
        Assert.Equal(expected, AccessGate.TokenMatches(AccessGate.Token(tokenCode), Code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4242")]
    public void TokenMatches_RejectsMissingOrRawCode(string? cookie) => Assert.False(AccessGate.TokenMatches(cookie, Code));

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/login.html", true)]
    [InlineData("/login", true)]
    [InlineData("/app.css", true)]
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    [InlineData("/timeline.html", false)]
    [InlineData("/api/rhythm", false)]
    public void IsPublic(string path, bool expected) => Assert.Equal(expected, AccessGate.IsPublic(path));

    [Theory]
    [InlineData("/timeline.html", "/timeline.html")]
    [InlineData("/timeline.html?w=3h", "/timeline.html?w=3h")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("timeline.html", "/")]
    public void SafeNext(string? next, string expected) => Assert.Equal(expected, AccessGate.SafeNext(next));
}
