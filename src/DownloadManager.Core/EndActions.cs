namespace DownloadManager.Core;

/// <summary>What the PC can be asked to do; the real one calls Windows, tests and the simulation mode only record.</summary>
public interface IPowerController
{
    void Sleep();
    void Hibernate();
    void Shutdown();
}

/// <summary>
/// Runs a queue's end action: a countdown the user can cancel comes first, then running downloads are saved,
/// then the action. Returns true when the action was carried out.
/// </summary>
public sealed class EndActionRunner(
    IPowerController power,
    Func<EndAction, TimeSpan, Task<bool>> countdown,
    Func<Task> saveDownloads,
    Action closeApp)
{
    public static readonly TimeSpan CountdownLength = TimeSpan.FromSeconds(60);

    public async Task<bool> RunAsync(EndAction action)
    {
        if (action == EndAction.None)
            return false;
        if (!await countdown(action, CountdownLength))
            return false; // cancelled

        // Before the app ends, running downloads are checkpointed and stopped so they resume on the next start.
        // Sleep and hibernation keep the app alive: progress is saved every second anyway, and the connections
        // that drop while the PC sleeps are retried on waking.
        if (action is EndAction.CloseApp or EndAction.Shutdown)
            await saveDownloads();
        switch (action)
        {
            case EndAction.CloseApp:
                closeApp();
                break;
            case EndAction.Sleep:
                power.Sleep();
                break;
            case EndAction.Hibernate:
                power.Hibernate();
                break;
            case EndAction.Shutdown:
                power.Shutdown();
                break;
        }
        return true;
    }
}
