using System.Net;
using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class EndOfLifeTests
{
    // Shapes copied from live https://endoflife.date/api/{product}.json responses: "eol" is a date or false,
    // "lts" is a date or a boolean.
    private const string NodeJson = """
        [{"cycle":"26","releaseDate":"2026-05-05","lts":"2026-10-28","eol":"2029-04-30","latest":"26.13.0"},
         {"cycle":"25","releaseDate":"2025-10-15","eol":"2026-06-01","latest":"25.9.0","lts":false},
         {"cycle":"20","releaseDate":"2023-04-18","lts":"2023-10-24","eol":"2026-04-30","latest":"20.20.0"}]
        """;

    private const string GoJson = """
        [{"cycle":"1.27","releaseDate":"2026-08-19","eol":false,"latest":"1.27.3","lts":false},
         {"cycle":"1.26","releaseDate":"2026-02-10","eol":true,"latest":"1.26.10","lts":false}]
        """;

    private static readonly DateOnly Today = new(2026, 11, 2);

    private static IReadOnlyList<EolCycle> Cycles(string json) => EndOfLifeParser.Parse(json);

    [Fact]
    public void ParserReadsDatesAndBooleans()
    {
        var node = Cycles(NodeJson);
        Assert.Equal(new DateOnly(2029, 4, 30), node.Single(c => c.Cycle == "26").EndDate);
        var go = Cycles(GoJson);
        Assert.Null(go.Single(c => c.Cycle == "1.27").EndDate);
        Assert.False(go.Single(c => c.Cycle == "1.27").Ended);
        Assert.True(go.Single(c => c.Cycle == "1.26").Ended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"cycle\":\"1\"}")]
    public void ParserRejectsPayloadsThatAreNotACycleList(string json) =>
        Assert.Throws<InvalidDataException>(() => EndOfLifeParser.Parse(json));

    [Fact]
    public void ParserSkipsCyclesWithoutAnIdentifier() =>
        Assert.Single(Cycles("""[{"eol":"2020-01-01"},{"cycle":"3","eol":false}]"""));

    [Theory]
    [InlineData("20.11.0", EolState.Ended, "20")]
    [InlineData("25.1.0", EolState.Ended, "25")]
    [InlineData("26.0.0", EolState.Supported, "26")]
    public void AssessmentMatchesTheMajorCycleAndComparesDates(string version, EolState state, string cycle)
    {
        var result = EndOfLifeAssessor.Assess(Cycles(NodeJson), ToolKind.Node, version, Today);
        Assert.Equal(state, result.State);
        Assert.Equal(cycle, result.Cycle);
    }

    [Fact]
    public void ADateWithinNinetyDaysIsEndingSoon()
    {
        var result = EndOfLifeAssessor.Assess(Cycles(NodeJson), ToolKind.Node, "25.0.0", new DateOnly(2026, 4, 1));
        Assert.Equal(EolState.EndingSoon, result.State);
        Assert.Equal(new DateOnly(2026, 6, 1), result.EndDate);
    }

    [Fact]
    public void TheLongestMatchingCycleWins()
    {
        var cycles = Cycles("""[{"cycle":"3","eol":"2030-01-01"},{"cycle":"3.9","eol":"2025-10-31"}]""");
        Assert.Equal("3.9", EndOfLifeAssessor.Assess(cycles, ToolKind.Python, "3.9.18", Today).Cycle);
        Assert.Equal("3", EndOfLifeAssessor.Assess(cycles, ToolKind.Python, "3.13.0", Today).Cycle);
    }

    [Fact]
    public void BooleanEndedAndNeverEndingBothResolve()
    {
        Assert.Equal(EolState.Ended, EndOfLifeAssessor.Assess(Cycles(GoJson), ToolKind.Go, "1.26.4", Today).State);
        Assert.Equal(EolState.Supported, EndOfLifeAssessor.Assess(Cycles(GoJson), ToolKind.Go, "1.27.1", Today).State);
    }

    [Fact]
    public void LegacyJavaVersionNumbersMapToTheirMajor()
    {
        var cycles = Cycles("""[{"cycle":"8","eol":"2030-12-31"}]""");
        Assert.Equal("8", EndOfLifeAssessor.Assess(cycles, ToolKind.Jdk, "1.8.0", Today).Cycle);
    }

    [Theory]
    [InlineData("99.0.0")]
    [InlineData("weird-build")]
    [InlineData("")]
    public void AnUnmatchedVersionIsUnknownNotGuessed(string version) =>
        Assert.Equal(EolState.Unknown, EndOfLifeAssessor.Assess(Cycles(NodeJson), ToolKind.Node, version, Today).State);

    [Fact]
    public void NoDataIsUnknown() =>
        Assert.Equal(EolState.Unknown, EndOfLifeAssessor.Assess(null, ToolKind.Node, "20.0.0", Today).State);

    [Fact]
    public void DescriptionsAreHumanReadable()
    {
        Assert.Equal("end of life since 2026-04-30", EndOfLifeAssessor.Assess(Cycles(NodeJson), ToolKind.Node, "20.1.0", Today).Describe());
        Assert.Equal("supported until 2029-04-30", EndOfLifeAssessor.Assess(Cycles(NodeJson), ToolKind.Node, "26.0.0", Today).Describe());
        Assert.Equal("supported", EndOfLifeAssessor.Assess(Cycles(GoJson), ToolKind.Go, "1.27.0", Today).Describe());
        Assert.Equal("no end-of-life data", EndOfLifeAssessor.Assess(null, ToolKind.Node, "20.0.0", Today).Describe());
    }

    [Theory]
    [InlineData(ToolKind.Node, "nodejs")]
    [InlineData(ToolKind.Jdk, "eclipse-temurin")]
    [InlineData(ToolKind.DotNetSdk, "dotnet")]
    [InlineData(ToolKind.Rustc, "rust")]
    [InlineData(ToolKind.Python, "python")]
    public void ToolsMapToVerifiedProductIds(ToolKind tool, string product) =>
        Assert.Equal(product, EndOfLifeProducts.For(tool));

    [Theory]
    [InlineData(ToolKind.Maven)]
    [InlineData(ToolKind.Cmake)]
    [InlineData(ToolKind.Zig)]
    [InlineData(ToolKind.Npm)]
    public void ToolsWithoutAnEndpointHaveNoProduct(ToolKind tool) => Assert.Null(EndOfLifeProducts.For(tool));

    // ---- service (fake HTTP, no network) ----

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RefreshFetchesEachProductOnceCachesItAndReadsItBack()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new FakeHandler(_ => Json(NodeJson));
            var service = new EndOfLifeService(directory, handler);
            var now = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

            var result = service.Refresh([ToolKind.Node, ToolKind.Node, ToolKind.Maven], now);

            Assert.Equal(1, result.Fetched);
            Assert.Empty(result.Errors);
            Assert.Equal(["https://endoflife.date/api/nodejs.json"], handler.Requests);
            Assert.Equal(3, service.Load()[ToolKind.Node].Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AFreshCacheIsNotFetchedAgainButAStaleOneIs()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new FakeHandler(_ => Json(NodeJson));
            var service = new EndOfLifeService(directory, handler, TimeSpan.FromDays(7));
            var start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
            service.Refresh([ToolKind.Node], start);

            var fresh = service.Refresh([ToolKind.Node], start.AddDays(6));
            Assert.Equal((0, 1), (fresh.Fetched, fresh.FromCache));
            Assert.Single(handler.Requests);

            var stale = service.Refresh([ToolKind.Node], start.AddDays(8));
            Assert.Equal(1, stale.Fetched);
            Assert.Equal(2, handler.Requests.Count);

            var forced = service.Refresh([ToolKind.Node], start.AddDays(8), force: true);
            Assert.Equal(1, forced.Fetched);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void HttpErrorsAreReportedWithStatusAndBodyAndKeepOldData()
    {
        var directory = TempDirectory();
        try
        {
            var service = new EndOfLifeService(directory, new FakeHandler(_ => Json(NodeJson)));
            var start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
            service.Refresh([ToolKind.Node], start);

            var failing = new EndOfLifeService(directory, new FakeHandler(_ => Json("{\"error\":\"rate limited\"}", HttpStatusCode.TooManyRequests)));
            var result = failing.Refresh([ToolKind.Node], start.AddDays(30));

            var error = Assert.Single(result.Errors);
            Assert.Contains("429", error);
            Assert.Contains("rate limited", error);
            Assert.Contains("nodejs", error);
            Assert.Equal(3, failing.Load()[ToolKind.Node].Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AnUnparseableBodyIsAnErrorNotACachedEmptyList()
    {
        var directory = TempDirectory();
        try
        {
            var service = new EndOfLifeService(directory, new FakeHandler(_ => Json("<html>Page not Found</html>")));
            var result = service.Refresh([ToolKind.Node], DateTimeOffset.UtcNow);
            Assert.Equal(0, result.Fetched);
            Assert.Contains("nodejs", Assert.Single(result.Errors));
            Assert.Empty(service.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ANetworkFailureIsReportedWithItsMessage()
    {
        var directory = TempDirectory();
        try
        {
            var service = new EndOfLifeService(directory, new FakeHandler(_ => throw new HttpRequestException("name resolution failed")));
            var result = service.Refresh([ToolKind.Go], DateTimeOffset.UtcNow);
            Assert.Contains("name resolution failed", Assert.Single(result.Errors));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ACorruptCacheFileIsIgnoredNotFatal()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "eol-cache.json"), "{ broken");
            var service = new EndOfLifeService(directory, new FakeHandler(_ => Json(NodeJson)));
            Assert.Empty(service.Load());
            Assert.Equal(1, service.Refresh([ToolKind.Node], DateTimeOffset.UtcNow).Fetched);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
