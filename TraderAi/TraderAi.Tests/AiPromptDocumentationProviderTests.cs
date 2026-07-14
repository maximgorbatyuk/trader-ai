using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiPromptDocumentationProviderTests : IDisposable
{
    private static readonly string[] CoreDocuments =
    {
        "docs/roles/ai-agent.md",
        "docs/roles/individual.md",
        "docs/rules/share-price-formation.md",
        "docs/rules/trading-days.md",
        "docs/rules/luld.md",
        "docs/logic/settlement.md",
        "docs/logic/margin.md",
        "docs/logic/bank-loans.md",
        "docs/logic/sector-sentiment.md",
    };

    private readonly List<string> tempRoots = new();

    [Fact]
    public void FirstRequestReadsRequiredFiles()
    {
        var root = CreateTempRoot(includeFund: false);
        var provider = Provider(root, new MutableTimeProvider(Start));

        var documents = provider.GetDocuments(includeFundDocuments: false);

        Assert.Equal(CoreDocuments.Length, documents.Count);
        Assert.Contains(documents, document => document.SourcePath == "docs/roles/ai-agent.md");
        Assert.All(documents, document => Assert.False(string.IsNullOrWhiteSpace(document.Content)));
    }

    [Fact]
    public void EditWithinWindowStillReturnsCachedContent()
    {
        var root = CreateTempRoot(includeFund: false);
        var time = new MutableTimeProvider(Start);
        var provider = Provider(root, time);

        var first = provider.GetDocument("docs/logic/margin.md");
        WriteDoc(root, "docs/logic/margin.md", "CHANGED");
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(first, provider.GetDocument("docs/logic/margin.md"));
    }

    [Fact]
    public void ReloadsAfterExpiration()
    {
        var root = CreateTempRoot(includeFund: false);
        var time = new MutableTimeProvider(Start);
        var provider = Provider(root, time);

        provider.GetDocument("docs/logic/margin.md");
        WriteDoc(root, "docs/logic/margin.md", "CHANGED");
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("CHANGED", provider.GetDocument("docs/logic/margin.md"));
    }

    [Fact]
    public void FrequentReadsDoNotExtendExpiration()
    {
        var root = CreateTempRoot(includeFund: false);
        var time = new MutableTimeProvider(Start);
        var provider = Provider(root, time);

        provider.GetDocument("docs/logic/margin.md");
        time.Advance(TimeSpan.FromMinutes(4));
        provider.GetDocument("docs/logic/margin.md");
        provider.GetDocument("docs/logic/margin.md");
        WriteDoc(root, "docs/logic/margin.md", "CHANGED");
        time.Advance(TimeSpan.FromMinutes(2));

        // Absolute expiration means the entry expires five minutes after the first read regardless of hits.
        Assert.Equal("CHANGED", provider.GetDocument("docs/logic/margin.md"));
    }

    [Fact]
    public void FundDocumentsAreIncludedOnlyForFundMembers()
    {
        var root = CreateTempRoot(includeFund: true);
        var provider = Provider(root, new MutableTimeProvider(Start));

        Assert.Equal(CoreDocuments.Length, provider.GetDocuments(includeFundDocuments: false).Count);

        var withFund = provider.GetDocuments(includeFundDocuments: true);
        Assert.Equal(CoreDocuments.Length + 2, withFund.Count);
        Assert.Contains(withFund, document => document.SourcePath == "docs/roles/fund-member.md");
        Assert.Contains(withFund, document => document.SourcePath == "docs/roles/collective-fund.md");
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/plans/ai-traders.md")]
    [InlineData("docs/prompts/system.md")]
    [InlineData("../../etc/passwd")]
    public void DisallowedPathsAreRejected(string path)
    {
        var root = CreateTempRoot(includeFund: false);
        var provider = Provider(root, new MutableTimeProvider(Start));

        Assert.Throws<InvalidOperationException>(() => provider.GetDocument(path));
    }

    [Fact]
    public void MissingRequiredDocumentThrows()
    {
        var root = CreateTempRoot(includeFund: false);
        File.Delete(Path.Combine(root, "logic", "margin.md"));
        var provider = Provider(root, new MutableTimeProvider(Start));

        Assert.Throws<InvalidOperationException>(() => provider.GetDocuments(includeFundDocuments: false));
    }

    [Fact]
    public void MissingRootThrows()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-docs-missing-" + Guid.NewGuid().ToString("N"));
        var provider = Provider(root, new MutableTimeProvider(Start));

        Assert.Throws<InvalidOperationException>(() => provider.GetDocuments(includeFundDocuments: false));
    }

    private static DateTimeOffset Start => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private AiPromptDocumentationProvider Provider(string root, TimeProvider timeProvider)
        => new(
            Options.Create(new AiTradingOptions { DocumentationRoot = root }),
            new TestHostEnvironment { ContentRootPath = Path.GetTempPath() },
            timeProvider,
            NullLogger<AiPromptDocumentationProvider>.Instance);

    private string CreateTempRoot(bool includeFund)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-docs-" + Guid.NewGuid().ToString("N"));
        tempRoots.Add(root);
        foreach (var document in CoreDocuments)
        {
            WriteDoc(root, document, $"# {document}\nContent for {document}.");
        }

        if (includeFund)
        {
            WriteDoc(root, "docs/roles/fund-member.md", "# fund-member");
            WriteDoc(root, "docs/roles/collective-fund.md", "# collective-fund");
        }

        return root;
    }

    private static void WriteDoc(string root, string allowlistPath, string content)
    {
        var relative = allowlistPath["docs/".Length..];
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        foreach (var root in tempRoots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "TraderAi.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }
}
