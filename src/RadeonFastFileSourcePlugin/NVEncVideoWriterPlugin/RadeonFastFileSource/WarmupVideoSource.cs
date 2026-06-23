using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal sealed class WarmupVideoSource(IVideoFileSource source, string backend) : IDisposable
{
    private bool detached;

    public IVideoFileSource Source { get; } = source;

    public string Backend { get; } = backend;

    public void Detach()
    {
        detached = true;
    }

    public void Dispose()
    {
        if (!detached)
            Source.Dispose();
    }
}

