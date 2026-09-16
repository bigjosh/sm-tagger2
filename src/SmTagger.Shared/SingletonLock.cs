namespace SmTagger.Shared;

public sealed class SingletonLock : IDisposable
{
    private readonly FileStream stream;

    // Retain the exclusive handle rather than treating persistent file existence as ownership.
    private SingletonLock(FileStream stream)
    {
        this.stream = stream;
    }

    // VERSION-SENSITIVE-004: Windows sharing excludes every other same-role invocation.
    public static SingletonLock Acquire(string lockPath)
    {
        return new SingletonLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    // Release singleton ownership while leaving the persistent lock filename intact.
    public void Dispose()
    {
        stream.Dispose();
    }
}
