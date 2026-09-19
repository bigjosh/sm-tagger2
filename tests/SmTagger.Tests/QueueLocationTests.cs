using System.Security.Cryptography;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class QueueLocationTests
{
    // Real workspace and queue-lock failures release the first lock before any trace, configuration, or mail mutation.
    [Theory]
    [InlineData("workspace-file")]
    [InlineData("queue-lock-directory")]
    public void StartupFilesystemFailureReleasesDataLockWithoutTouchingProtectedFiles(string failure)
    {
        using var fixture = new ProcessorFixture();
        var original = fixture.WriteMessage("queued");
        string hdr = Path.Combine(fixture.ProcessDirectory, "queued.hdr");
        string eml = Path.Combine(fixture.ProcessDirectory, "queued.eml");
        string queueLock = Path.Combine(fixture.WorkDirectory, "sm-tagger.lock");
        byte[] blocker = "Existing workspace blocker must remain unchanged."u8.ToArray();
        if (failure == "workspace-file")
        {
            string savedHdr = Path.Combine(fixture.Root, "saved-queued.hdr");
            string savedEml = Path.Combine(fixture.Root, "saved-queued.eml");
            File.Move(hdr, savedHdr);
            File.Move(eml, savedEml);
            hdr = savedHdr;
            eml = savedEml;
            Directory.Delete(fixture.ProcessDirectory);
            Directory.Delete(fixture.WorkDirectory);
            File.WriteAllBytes(fixture.WorkDirectory, blocker);
        }
        else
        {
            Directory.CreateDirectory(queueLock);
        }
        string logPath = Path.Combine(fixture.DataDirectory, "log.txt");
        byte[] log = "Existing trace is not opened before both locks are held.\r\n"u8.ToArray();
        File.WriteAllBytes(logPath, log);
        string[] configuration = ConfigurationSnapshot(fixture);

        Assert.Equal(1, SmTagger.Program.Main([fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), fixture.SpoolDirectory, "queued"]));

        Assert.Equal(log, File.ReadAllBytes(logPath));
        Assert.Equal(configuration, ConfigurationSnapshot(fixture));
        Assert.Equal(original.Hdr, File.ReadAllBytes(hdr));
        Assert.Equal(original.Eml, File.ReadAllBytes(eml));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        if (failure == "workspace-file")
            Assert.Equal(blocker, File.ReadAllBytes(fixture.WorkDirectory));
        else
        {
            Assert.Empty(Directory.GetFileSystemEntries(queueLock));
            Assert.Equal(2, Directory.GetFiles(fixture.ProcessDirectory).Length);
        }
        using SingletonLock releasedData = SingletonLock.Acquire(Path.Combine(fixture.DataDirectory, "sm-tagger.lock"));
    }

    // Neither processor resumes old datadir queues, while fresh work uses the nested Proc queue and failure directory.
    [Fact]
    public void OldDatadirMessageTreesRemainUntouchedWithoutFallback()
    {
        using var fixture = new ProcessorFixture();
        string oldProcess = Path.Combine(fixture.DataDirectory, "process");
        string oldFailed = Path.Combine(fixture.DataDirectory, "failed");
        Directory.CreateDirectory(oldProcess);
        Directory.CreateDirectory(oldFailed);
        var old = fixture.WriteMessage("old-only");
        foreach (string extension in new[] { ".eml", ".hdr" })
            File.Move(Path.Combine(fixture.ProcessDirectory, "old-only" + extension), Path.Combine(oldProcess, "old-only" + extension));
        byte[] retainedHdr = "Failed\r\nold retained metadata"u8.ToArray();
        byte[] retainedEml = [0, 255, 13, 10];
        File.WriteAllBytes(Path.Combine(oldFailed, "rejected.hdr.sort"), retainedHdr);
        File.WriteAllBytes(Path.Combine(oldFailed, "rejected.eml"), retainedEml);

        Assert.Equal(1, SmTagger.Program.Main([fixture.DataDirectory, fixture.SpoolDirectory, "old-only"]));
        fixture.WriteMessage("fresh");
        var sorter = new SorterProcessor(fixture.DataDirectory, fixture.SpoolDirectory, fixture.Errors);
        string proc = Path.Combine(fixture.SpoolDirectory, "proc");
        foreach (string extension in new[] { ".eml", ".hdr" })
            File.Move(Path.Combine(fixture.ProcessDirectory, "fresh" + extension), Path.Combine(proc, "fresh" + extension));
        Assert.Equal(MessageOutcome.Succeeded, sorter.Process("fresh"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "fresh.hdr")));
        Assert.Equal(0, SmTagger.Program.Main([fixture.DataDirectory, fixture.SpoolDirectory, "fresh"]));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "freshc1.hdr")));

        byte[] failedHdr = "Failed\r\nnew retained metadata"u8.ToArray();
        byte[] failedEml = [42, 13, 10];
        File.WriteAllBytes(Path.Combine(proc, "rejected.hdr"), failedHdr);
        File.WriteAllBytes(Path.Combine(proc, "rejected.eml"), failedEml);
        Assert.Equal(MessageOutcome.Failed, sorter.Process("rejected"));
        Assert.Equal(failedHdr, File.ReadAllBytes(Path.Combine(fixture.FailedDirectory, "rejected.hdr.sort")));
        Assert.Equal(failedEml, File.ReadAllBytes(Path.Combine(fixture.FailedDirectory, "rejected.eml")));

        Assert.Equal(old.Hdr, File.ReadAllBytes(Path.Combine(oldProcess, "old-only.hdr")));
        Assert.Equal(old.Eml, File.ReadAllBytes(Path.Combine(oldProcess, "old-only.eml")));
        Assert.Equal(retainedHdr, File.ReadAllBytes(Path.Combine(oldFailed, "rejected.hdr.sort")));
        Assert.Equal(retainedEml, File.ReadAllBytes(Path.Combine(oldFailed, "rejected.eml")));
        Assert.Equal(2, Directory.GetFiles(oldProcess).Length);
        Assert.Equal(2, Directory.GetFiles(oldFailed).Length);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // Either shared resource excludes a differently configured tagger before it traces or claims mail.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaggerLocksRejectSharedSpoolOrSharedDatadirBeforeLogging(bool sameSpool)
    {
        using var owner = new ProcessorFixture();
        using var other = new ProcessorFixture();
        ProcessorFixture queue = sameSpool ? owner : other;
        string data = sameSpool ? other.DataDirectory : owner.DataDirectory;
        string spool = queue.SpoolDirectory;
        var original = queue.WriteMessage("queued");
        string logPath = Path.Combine(data, "log.txt");
        byte[] log = "Existing trace bytes stay unchanged until both resources are available.\r\n"u8.ToArray();
        File.WriteAllBytes(logPath, log);
        using (SingletonLock ownerData = SingletonLock.Acquire(Path.Combine(owner.DataDirectory, "sm-tagger.lock")))
        using (SingletonLock ownerQueue = SingletonLock.Acquire(Path.Combine(owner.WorkDirectory, "sm-tagger.lock")))
        {
            Assert.Equal(1, SmTagger.Program.Main([data, "-l", logPath, spool, "queued"]));
            Assert.Equal(log, File.ReadAllBytes(logPath));
            Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(queue.ProcessDirectory, "queued.hdr")));
            Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(queue.ProcessDirectory, "queued.eml")));
            Assert.False(Directory.Exists(Path.Combine(data, "tag-addresses")));
            Assert.Empty(Directory.GetFiles(spool));
            if (sameSpool)
            {
                // Queue-lock rejection must release the newly acquired, different data lock.
                using SingletonLock releasedContenderData = SingletonLock.Acquire(Path.Combine(data, "sm-tagger.lock"));
            }
            else
            {
                Assert.False(File.Exists(Path.Combine(queue.WorkDirectory, "sm-tagger.lock")));
            }
        }

        Assert.Equal(0, SmTagger.Program.Main([data, "-l", logPath, spool, "queued"]));
        Assert.True(File.Exists(Path.Combine(spool, "queuedc1.hdr")));
        Assert.Empty(Directory.GetFiles(queue.ProcessDirectory));
        using SingletonLock releasedData = SingletonLock.Acquire(Path.Combine(data, "sm-tagger.lock"));
        using SingletonLock releasedQueue = SingletonLock.Acquire(Path.Combine(queue.WorkDirectory, "sm-tagger.lock"));
    }

    // Snapshot all sender configuration identities and bytes independently of the mutable lock and trace files.
    private static string[] ConfigurationSnapshot(ProcessorFixture fixture)
    {
        string root = Path.Combine(fixture.DataDirectory, "senders");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();
    }
}
