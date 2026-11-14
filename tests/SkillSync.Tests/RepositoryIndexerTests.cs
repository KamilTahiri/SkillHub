using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

public sealed class RepositoryIndexerTests : IDisposable
{
    private const string Commit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-indexer-" + Guid.NewGuid().ToString("N"));

    private static readonly Dictionary<string, byte[]> Repository = new()
    {
        ["README.md"] = Text("# Fixture Skills\n\nA collection of agent skills for tests.\n"),
        ["LICENSE"] = Text("MIT License"),
        ["AGENTS.md"] = Text("# Repository rules\n\nAlways run the tests.\n"),
        ["docs/guide.md"] = Text("# Guide\n\nHow to use the skills.\n"),
        ["skills/pdf/SKILL.md"] = Text("---\nname: pdf\ndescription: Work with PDF files.\nlicense: Apache-2.0\n---\n# PDF\n\nRead [reference](reference.md) and [forms](forms.md).\n"),
        ["skills/pdf/reference.md"] = Text("Reference body."),
        ["skills/pdf/forms.md"] = Text("Forms body."),
        ["skills/pdf/LICENSE.txt"] = Text("Apache License"),
        ["skills/pdf/scripts/run.py"] = Text("print('ok')"),
        ["skills/pdf/logo.png"] = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01],
        [".github/instructions/csharp.instructions.md"] = Text("---\napplyTo: '**/*.cs'\ndescription: C# conventions\n---\nUse file-scoped namespaces.\n"),
        ["prompts/review.prompt.md"] = Text("# Review a pull request\n\nCheck **correctness** before style.\n"),
        ["commands/deploy.md"] = Text("Deploy the app to staging.\n"),
        ["node_modules/pkg/README.md"] = Text("# Ignored\n"),
        ["CHANGELOG.md"] = Text("# Changes\n"),
        ["website/src/content/learning/agents.md"] = Text("# A website page\n"),
        ["packages/cli/test/fixtures/README.md"] = Text("# Fixture output\n")
    };

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task TarballIndexClassifiesEveryFormatAndKeepsSiblingFiles()
    {
        using var github = new Fixture();
        var indexer = new RepositoryIndexer(new HttpClient(github), _root);
        var index = await indexer.GetIndexAsync("fixture", "repo", Commit, CancellationToken.None);

        Assert.Equal("tarball", index.Mode);
        Assert.DoesNotContain(github.Requests, uri => uri.AbsolutePath.Contains("/git/trees/"));
        Assert.Equal("README.md", index.ReadmePath);
        Assert.Contains("collection of agent skills", index.ReadmeExcerpt);
        Assert.Equal("LICENSE", index.LicensePath);

        var skill = Assert.Single(index.Documents, document => document.Kind == "skill");
        Assert.Equal("pdf", skill.Name);
        Assert.Equal("Work with PDF files.", skill.Description);
        Assert.Equal(["LICENSE.txt", "forms.md", "reference.md", "scripts/run.py"], skill.Files);
        Assert.Equal(["logo.png"], skill.ExcludedFiles);

        Assert.Equal("instructions", Assert.Single(index.Documents, document => document.Path.EndsWith(".instructions.md")).Kind);
        var prompt = Assert.Single(index.Documents, document => document.Kind == "prompt");
        Assert.Equal("Review a pull request", prompt.Title);
        Assert.Equal("Check correctness before style.", prompt.Description);
        Assert.False(prompt.HasFrontmatter);
        Assert.Equal("review", prompt.Name);
        Assert.Equal("command", Assert.Single(index.Documents, document => document.Path == "commands/deploy.md").Kind);
        Assert.Equal("project-rules", Assert.Single(index.Documents, document => document.Path == "AGENTS.md").Kind);
        Assert.Contains(index.Documents, document => document.Path == "docs/guide.md" && document.Group == "docs");
        Assert.DoesNotContain(index.Documents, document => document.Path.StartsWith("node_modules/"));
        Assert.DoesNotContain(index.Documents, document => document.Path == "skills/pdf/reference.md");
        // Housekeeping files, deep website pages and test fixtures are not listed as documents.
        Assert.DoesNotContain(index.Documents, document => document.Path is "CHANGELOG.md" or "website/src/content/learning/agents.md"
            or "packages/cli/test/fixtures/README.md");
        Assert.All(index.Documents, document => Assert.Matches(@"\.(md|mdc|markdown)$", document.Path.ToLowerInvariant()));

        // A second process reuses the SHA-keyed disk cache without any network access.
        using var offline = new Fixture();
        var cached = await new RepositoryIndexer(new HttpClient(offline), _root).GetIndexAsync("fixture", "repo", Commit, CancellationToken.None);
        Assert.Empty(offline.Requests);
        Assert.Equal(index.Documents.Count, cached.Documents.Count);
        Assert.Equal("Forms body.", await new RepositoryIndexer(new HttpClient(offline), _root)
            .ReadTextAsync(cached, "skills/pdf/forms.md", CancellationToken.None));
        Assert.Empty(offline.Requests);
    }

    [Fact]
    public async Task RefsRevalidateWithETagsAndBranchNamesMayContainSlashes()
    {
        using var github = new Fixture();
        var indexer = new RepositoryIndexer(new HttpClient(github), _root, TimeSpan.Zero);
        Assert.Equal(Commit, await indexer.ResolveCommitAsync("fixture", "repo", "main", CancellationToken.None));
        Assert.Equal(Commit, await indexer.ResolveCommitAsync("fixture", "repo", "main", CancellationToken.None));
        Assert.Equal(1, github.NotModified);

        var (sha, path) = await indexer.ResolveRefAndPathAsync("fixture", "repo", ["feature", "x", "skills", "pdf"], CancellationToken.None);
        Assert.Equal(Commit, sha);
        Assert.Equal("skills/pdf", path);
    }

    [Fact]
    public async Task SkillsShResultsOpenFromTheirSourceRepository()
    {
        using var github = new Fixture();
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var importer = new SkillImporterService(engine, new HttpClient(github));

        var listed = Assert.Single((await importer.BrowseRepositoryAsync("skills.sh", "pdf")).Skills);
        Assert.Equal("fixture/repo", listed.Repo);
        Assert.Equal(1234, listed.Installs);
        var preview = await importer.PreviewCommunitySkillAsync(listed.Repo, listed.Path, listed.GitRef);
        Assert.Equal("skills/pdf/SKILL.md", preview.Path);
        Assert.Equal(Commit, preview.Revision);

        // Keyword search folds skills.sh in, skipping hosts that are not GitHub repositories.
        var search = await importer.SearchCommunityAsync("pdf");
        Assert.Contains(search.Skills, row => row.Repo == "fixture/repo");
        Assert.DoesNotContain(search.Skills, row => row.Repo.Contains("example.com"));
    }

    [Fact]
    public async Task ImportKeepsRootLevelSiblingFilesInCanonicalStorage()
    {
        using var github = new Fixture();
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var importer = new SkillImporterService(engine, new HttpClient(github));

        var browse = await importer.BrowseRepositoryAsync("https://github.com/fixture/repo/tree/feature/x/skills");
        Assert.Equal(Commit, browse.Repository!.Sha);
        var row = Assert.Single(browse.Skills);
        Assert.Equal(4, row.FileCount);

        var copilot = await importer.PreviewCommunitySkillAsync("fixture/repo", ".github/instructions/csharp.instructions.md", Commit);
        Assert.Equal("instructions", copilot.Kind);
        var prompt = await importer.PrepareImportAsync(new(Repo: "fixture/repo", Path: "prompts/review.prompt.md", GitRef: Commit));
        Assert.Equal("review", prompt.Name);
        Assert.Equal("Review a pull request", prompt.Title);

        var plan = await importer.PrepareImportAsync(new(Repo: "fixture/repo", Path: "skills/pdf/SKILL.md", GitRef: Commit));
        Assert.Equal("Forms body.", plan.SupportingFiles["forms.md"]);
        Assert.Equal("Apache License", plan.SupportingFiles["LICENSE.txt"]);
        Assert.Contains(plan.SkippedFiles, item => item.StartsWith("logo.png", StringComparison.Ordinal));
        var saved = await importer.ApplyImportAsync(plan.PlanId, new(plan.Revision, null, []));
        var directory = Path.Combine(_root, ".skillhub", "skills", saved.Name);
        Assert.Equal("Reference body.", File.ReadAllText(Path.Combine(directory, "reference.md")));
        Assert.Equal("print('ok')", File.ReadAllText(Path.Combine(directory, "scripts", "run.py")));
        Assert.Equal("Forms body.", engine.GetSkill(saved.Name)!.SupportingFiles["forms.md"]);
    }

    /// <summary>Runs against github.com only when SKILLHUB_LIVE_GITHUB=1, so CI and offline runs are unaffected.</summary>
    [Fact]
    public async Task LiveGitHubRepositoriesIndexThroughCodeload()
    {
        if (Environment.GetEnvironmentVariable("SKILLHUB_LIVE_GITHUB") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SkillHub-tests/1.0");
        var indexer = new RepositoryIndexer(http, _root);
        foreach (var (owner, repo) in new[] { ("anthropics", "skills"), ("github", "awesome-copilot") })
        {
            var sha = await indexer.ResolveCommitAsync(owner, repo, "HEAD", CancellationToken.None);
            var index = await indexer.GetIndexAsync(owner, repo, sha, CancellationToken.None);
            Assert.Equal("tarball", index.Mode);
            Assert.Contains(index.Documents, document => document.Kind == "skill" && document.Description.Length > 0);
            var counts = string.Join(", ", index.Documents.GroupBy(document => document.Kind).Select(group => group.Key + "=" + group.Count()));
            Console.WriteLine($"{owner}/{repo}@{sha[..7]}: {index.Files.Count} files; {counts}");
            if (repo == "skills")
            {
                var pdf = index.Documents.FirstOrDefault(document => document.Path == "skills/pdf/SKILL.md");
                Assert.NotNull(pdf);
                Console.WriteLine("pdf bundle: " + string.Join(", ", pdf!.Files.Take(12)));
                Assert.Contains(pdf.Files, file => !file.Contains('/'));
            }
            else
            {
                Assert.Contains(index.Documents, document => document.Kind == "instructions");
            }
        }
    }

    [Fact]
    public async Task LiveExploreRowsAreMarkdownDocumentsOnly()
    {
        if (Environment.GetEnvironmentVariable("SKILLHUB_LIVE_GITHUB") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SkillHub-tests/1.0");
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var importer = new SkillImporterService(engine, http);
        foreach (var repo in new[] { "anthropics/skills", "github/awesome-copilot", "vercel-labs/agent-skills" })
        {
            var rows = (await importer.BrowseRepositoryAsync(repo)).Skills;
            var byGroup = string.Join(", ", rows.GroupBy(row => row.Group).Select(group => group.Key + "=" + group.Count()));
            Console.WriteLine($"{repo}: {rows.Count} rows ({byGroup}); first five: " + string.Join(" | ", rows.Take(5).Select(row => row.Path)));
            Console.WriteLine("  agents-md rows: " + string.Join(" | ", rows.Where(row => row.Path.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase)).Take(8).Select(row => row.Path)));
            Console.WriteLine("  doc samples: " + string.Join(" | ", rows.Where(row => row.Group == "docs").Take(8).Select(row => row.Path)));
            Assert.All(rows, row => Assert.Matches(@"\.(md|mdc|markdown)$", row.Path.ToLowerInvariant()));
        }
        var directory = (await importer.BrowseRepositoryAsync("skills.sh", "react")).Skills;
        Console.WriteLine("skills.sh react: " + string.Join(" | ", directory.Take(6).Select(row => row.Repo + "/" + row.Path + " (" + row.Installs + ")")));
        var opened = await importer.PreviewCommunitySkillAsync(directory[0].Repo, directory[0].Path, directory[0].GitRef);
        Console.WriteLine("opened skills.sh row: " + opened.Repo + ":" + opened.Path + " · " + opened.SupportingPaths.Count + " files");
        var search = await importer.SearchCommunityAsync("python");
        Console.WriteLine("search python: " + string.Join(" | ", search.Skills.Take(12).Select(row => row.Repo + ":" + row.Path + " [" + row.Title + "]")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] Tarball()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = Commit }));
            foreach (var (path, bytes) in Repository)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "repo-" + Commit + "/" + path)
                {
                    DataStream = new MemoryStream(bytes)
                });
            }
        }
        return output.ToArray();
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public int NotModified;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (uri.Host == "codeload.github.com" && path == $"/fixture/repo/tar.gz/{Commit}")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tarball()) });
            if (uri.Host == "skills.sh" && path == "/api/search")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    skills = new object[]
                    {
                        new { id = "fixture/repo/pdf", source = "fixture/repo", skillId = "pdf", name = "pdf", installs = 1234 },
                        new { id = "example.com/pdf", source = "example.com", skillId = "pdf", name = "pdf", installs = 99 }
                    }
                })) });
            if (path is "/repos/fixture/repo/commits/main" or "/repos/fixture/repo/commits/feature/x" or "/repos/fixture/repo/commits/HEAD")
            {
                if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\""))
                {
                    Interlocked.Increment(ref NotModified);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { sha = Commit })) };
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
