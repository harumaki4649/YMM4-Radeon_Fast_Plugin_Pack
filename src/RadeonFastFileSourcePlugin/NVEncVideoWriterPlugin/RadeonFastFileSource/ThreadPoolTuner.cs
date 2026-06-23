namespace RadeonFastFileSourcePlugin;

internal static class ThreadPoolTuner
{
    private static bool applied;

    public static void TryApply()
    {
        if (applied)
            return;

        applied = true;

        try
        {
            var settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnableThreadPoolBoost)
            {
                FastFileSourceLog.Write("ThreadPool boost disabled by settings");
                return;
            }

            ThreadPool.GetMinThreads(out var currentWorker, out var currentIo);
            var requestedWorker = Math.Max(currentWorker, settings.ThreadPoolMinWorkerThreads);
            if (requestedWorker == currentWorker)
            {
                FastFileSourceLog.Write($"ThreadPool boost keep worker={currentWorker} io={currentIo}");
                return;
            }

            if (ThreadPool.SetMinThreads(requestedWorker, currentIo))
                FastFileSourceLog.Write($"ThreadPool boost applied worker={currentWorker}->{requestedWorker} io={currentIo}");
            else
                FastFileSourceLog.Write($"ThreadPool boost failed worker={currentWorker}->{requestedWorker} io={currentIo}");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"ThreadPool boost failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
