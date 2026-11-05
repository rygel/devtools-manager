using DevTools.Store;
using DevTools.Core.Model;
using Xunit;

namespace DevTools.Tests;

public sealed class StoreSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidLegacyFileStopsTheImportAndIsLeftUnchanged(bool isConfig)
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            var store = new SqliteStore(path);
            var target = isConfig ? store.LegacyConfigPath : store.LegacyIndexPath;
            File.WriteAllText(target, "{invalid");
            if (isConfig) Assert.Throws<InvalidDataException>(() => store.LoadConfig());
            else Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Equal("{invalid", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".migrated"));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ScanLockExcludesAnotherStoreUntilReleased()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new SqliteStore(path);
            var second = new SqliteStore(path);
            using (var held = first.TryAcquireScanLock())
            {
                Assert.NotNull(held);
                Assert.Null(second.TryAcquireScanLock());
            }
            using var next = second.TryAcquireScanLock();
            Assert.NotNull(next);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ConfigUpdateReplacesTheStoredConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["first"] });
            store.SaveConfig(new StoreConfig { Roots = ["second"] });
            Assert.Equal(["second"], new SqliteStore(path).LoadConfig().Roots);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ReadingMissingConfigWithoutCreationDoesNotWriteDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            Assert.Empty(store.LoadConfig(createIfMissing: false).Roots);
            Assert.False(File.Exists(store.DatabasePath));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ReadOnlyStoreDoesNotCreateItsDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteStore(path, createDirectory: false);
        Assert.Null(store.Load());
        Assert.Empty(store.LoadConfig(createIfMissing: false).Roots);
        Assert.Empty(store.ReadAudit());
        Assert.Null(store.Changes());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void LegacyJsonFilesAreImportedOnceAndRenamed()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            var store = new SqliteStore(path);
            File.WriteAllText(store.LegacyIndexPath, """{ "schemaVersion": 1, "projects": [ { "key": "k", "path": "/p", "state": "active" } ] }""");
            File.WriteAllText(store.LegacyConfigPath, """{ "schemaVersion": 1, "roots": ["/code"], "maxDepth": 7 }""");
            File.WriteAllText(store.LegacyAuditPath, """{"timestamp":"2026-01-01T00:00:00+00:00","planId":"p","action":"migrate","target":"t","outcome":"ok"}""" + "\n");

            Assert.Equal("k", Assert.Single(store.Load()!.Projects).Key);
            Assert.Equal(["/code"], store.LoadConfig().Roots);
            Assert.Equal("migrate", Assert.Single(store.ReadAudit()).Action);

            Assert.False(File.Exists(store.LegacyIndexPath));
            Assert.True(File.Exists(store.LegacyIndexPath + ".migrated"));

            // A second open must not import again.
            var reopened = new SqliteStore(path);
            Assert.Single(reopened.ReadAudit());
            Assert.Single(reopened.ScanHistory());
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ScanHistoryReportsAddedRemovedAndChangedProjects()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            var first = new IndexSnapshot
            {
                ScannedAt = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00"),
                Projects =
                [
                    new ProjectRecord { Key = "a", Path = "/code/a", Name = "a", GitBranch = "main" },
                    new ProjectRecord { Key = "b", Path = "/code/b", Name = "b" }
                ]
            };
            store.Save(first);
            Assert.Null(store.Changes()!.PreviousScanAt);

            var second = new IndexSnapshot
            {
                ScannedAt = DateTimeOffset.Parse("2026-01-02T10:00:00+00:00"),
                Projects =
                [
                    new ProjectRecord { Key = "a", Path = "/code/a", Name = "a", GitBranch = "feature" },
                    new ProjectRecord { Key = "c", Path = "/code/c", Name = "c" }
                ]
            };
            store.Save(second);

            var changes = store.Changes()!;
            Assert.Equal("c", Assert.Single(changes.Added).Key);
            Assert.Equal("b", Assert.Single(changes.Removed).Key);
            Assert.Equal("a", Assert.Single(changes.Changed).Key);
            Assert.Equal(2, store.ScanHistory().Count);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void SizesAndTimestampsDoNotCountAsAChange()
    {
        var before = new ProjectRecord { Key = "a", Path = "/a", SizeBytes = 10, LastSeenAt = DateTimeOffset.UnixEpoch };
        var after = new ProjectRecord { Key = "a", Path = "/a", SizeBytes = 999, LastSeenAt = DateTimeOffset.Now };
        Assert.Equal(SqliteStore.ProjectDigest(before), SqliteStore.ProjectDigest(after));
    }

    [Fact]
    public void VerificationDoesNotRunWhenAuditCannotBeWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var install = Path.Combine(root, "jdk-21");
            var bin = Path.Combine(install, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), "not executable");
            var store = new SqliteStore(Path.Combine(root, "store"));
            store.Save(new IndexSnapshot { Toolchains = [new Toolchain { Tool = ToolKind.Jdk, Path = install, Health = ToolchainHealth.Unverified }] });

            // A read-only database file makes every write fail, which is how an unwritable audit log looks to the store.
            File.SetAttributes(store.DatabasePath, FileAttributes.ReadOnly);
            try
            {
                var result = ToolchainVerification.Run(store, install);
                Assert.False(result.Succeeded);
                Assert.Contains("Audit log unavailable", result.Message);
            }
            finally { File.SetAttributes(store.DatabasePath, FileAttributes.Normal); }

            Assert.Equal(ToolchainHealth.Unverified, Assert.Single(store.Load()!.Toolchains).Health);
        }
        finally { Directory.Delete(root, true); }
    }
}
