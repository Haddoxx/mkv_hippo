using MkvHippo.Core.Scheduling;

namespace MkvHippo.Core.Tests;

public class AdaptiveSchedulerTests
{
    /// <summary>A job that signals when it starts and blocks until released.</summary>
    private sealed class ControlledJob
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started => _started.Task.IsCompleted;

        public void Release() => _release.TrySetResult();

        public Func<CancellationToken, Task> Body => _ =>
        {
            _started.TrySetResult();
            return _release.Task;
        };
    }

    private static List<ControlledJob> EnqueueControlled(AdaptiveScheduler scheduler, int count)
    {
        var jobs = Enumerable.Range(0, count).Select(_ => new ControlledJob()).ToList();
        foreach (var job in jobs)
            scheduler.Enqueue(job.Body);
        return jobs;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting until: " + because);
            await Task.Delay(5);
        }
    }

    /// <summary>Settle window for negative assertions ("nothing else started").</summary>
    private static Task SettleAsync() => Task.Delay(150);

    private static async Task DrainAsync(AdaptiveScheduler scheduler, IEnumerable<ControlledJob> jobs)
    {
        foreach (var job in jobs)
            job.Release();
        await scheduler.WaitForIdleAsync();
    }

    [Fact] // case (a)
    public async Task IncreasingTargetMidRunStartsAdditionalJobsImmediately()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 2);
        var jobs = EnqueueControlled(scheduler, 6);

        await WaitUntilAsync(() => jobs[0].Started && jobs[1].Started, "first two jobs start at target 2");
        await SettleAsync();
        Assert.False(jobs[2].Started);
        Assert.False(jobs[3].Started);
        Assert.Equal(2, scheduler.ActiveCount);

        scheduler.SetTarget(4);

        await WaitUntilAsync(() => jobs[2].Started && jobs[3].Started, "two more jobs start on scale-up");
        Assert.Equal(4, scheduler.ActiveCount);
        await SettleAsync();
        Assert.False(jobs[4].Started);
        Assert.False(jobs[5].Started);

        await DrainAsync(scheduler, jobs);
    }

    [Fact] // case (b)
    public async Task DecreasingTargetToOneDrainsWithoutKillingThenRunsStrictlySequentially()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 4);
        var jobs = EnqueueControlled(scheduler, 8);

        await WaitUntilAsync(() => jobs.Take(4).All(j => j.Started), "four jobs start at target 4");
        scheduler.SetTarget(1);

        // All four running jobs finish naturally; no backfill while active is above the target.
        jobs[0].Release();
        await WaitUntilAsync(() => scheduler.ActiveCount == 3, "one job drains");
        await SettleAsync();
        Assert.False(jobs[4].Started);

        jobs[1].Release();
        jobs[2].Release();
        await WaitUntilAsync(() => scheduler.ActiveCount == 1, "three jobs drained");
        await SettleAsync();
        Assert.False(jobs[4].Started);

        // Only once active drops below 1 does the next job dispatch — strictly one at a time.
        jobs[3].Release();
        await WaitUntilAsync(() => jobs[4].Started, "fifth job starts after full drain");
        Assert.Equal(1, scheduler.ActiveCount);
        await SettleAsync();
        Assert.False(jobs[5].Started);

        jobs[4].Release();
        await WaitUntilAsync(() => jobs[5].Started, "sixth job starts alone");
        Assert.Equal(1, scheduler.ActiveCount);
        await SettleAsync();
        Assert.False(jobs[6].Started);

        await DrainAsync(scheduler, jobs);
        Assert.True(jobs.All(j => j.Started));
    }

    [Fact] // case (c)
    public async Task DecreasingTargetFromFourToTwoBackfillsOnlyAfterTwoCompletions()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 4);
        var jobs = EnqueueControlled(scheduler, 8);

        await WaitUntilAsync(() => jobs.Take(4).All(j => j.Started), "four jobs start at target 4");
        scheduler.SetTarget(2);

        jobs[0].Release();
        await WaitUntilAsync(() => scheduler.ActiveCount == 3, "drained to 3");
        await SettleAsync();
        Assert.False(jobs[4].Started);

        jobs[1].Release();
        await WaitUntilAsync(() => scheduler.ActiveCount == 2, "drained to 2");
        await SettleAsync();
        Assert.False(jobs[4].Started);

        // Two completions later the pool is at target; from now on completions backfill 1:1.
        jobs[2].Release();
        await WaitUntilAsync(() => jobs[4].Started, "backfill resumes below target");
        Assert.Equal(2, scheduler.ActiveCount);
        await SettleAsync();
        Assert.False(jobs[5].Started);

        await DrainAsync(scheduler, jobs);
    }

    [Fact] // case (d)
    public async Task NoJobEverDispatchesWithActiveAboveTarget()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 3);
        var snapshots = new List<(int Active, int Target)>();
        var jobs = new List<ControlledJob>();

        Func<CancellationToken, Task> Instrumented(ControlledJob job) => ct =>
        {
            lock (snapshots)
                snapshots.Add((scheduler.ActiveCount, scheduler.Target));
            return job.Body(ct);
        };

        for (int i = 0; i < 12; i++)
        {
            var job = new ControlledJob();
            jobs.Add(job);
            scheduler.Enqueue(Instrumented(job));
        }

        await WaitUntilAsync(() => jobs.Take(3).All(j => j.Started), "initial dispatch");
        scheduler.SetTarget(1);
        jobs[0].Release();
        jobs[1].Release();
        await WaitUntilAsync(() => scheduler.ActiveCount == 1, "drain toward 1");
        jobs[2].Release();
        await WaitUntilAsync(() => jobs[3].Started, "sequential dispatch at 1");
        scheduler.SetTarget(4);
        await WaitUntilAsync(() => jobs.Take(7).All(j => j.Started), "scale-up dispatch at 4");
        scheduler.SetTarget(2);
        await DrainAsync(scheduler, jobs);

        Assert.Equal(12, snapshots.Count);
        Assert.All(snapshots, s => Assert.True(
            s.Active <= s.Target, $"job saw active={s.Active} > target={s.Target} at dispatch"));
    }

    [Fact] // case (e)
    public async Task QueueDrainsFully()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 3);
        int ran = 0;
        for (int i = 0; i < 25; i++)
        {
            scheduler.Enqueue(async _ =>
            {
                await Task.Yield();
                Interlocked.Increment(ref ran);
            });
        }

        await scheduler.WaitForIdleAsync();

        Assert.Equal(25, ran);
        Assert.Equal(0, scheduler.ActiveCount);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact] // case (f)
    public async Task CancellationStopsDispatchButLetsRunningJobsFinish()
    {
        using var cts = new CancellationTokenSource();
        var scheduler = new AdaptiveScheduler(initialTarget: 2, cts.Token);
        var jobs = EnqueueControlled(scheduler, 6);

        await WaitUntilAsync(() => jobs[0].Started && jobs[1].Started, "two jobs start");
        cts.Cancel();

        jobs[0].Release();
        jobs[1].Release();
        await scheduler.WaitForIdleAsync();

        Assert.False(jobs[2].Started);
        Assert.False(jobs[3].Started);
        Assert.False(jobs[4].Started);
        Assert.False(jobs[5].Started);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(0, scheduler.ActiveCount);
    }

    [Fact]
    public void TargetIsClampedToOneThroughFour()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 99);
        Assert.Equal(4, scheduler.Target);
        scheduler.SetTarget(0);
        Assert.Equal(1, scheduler.Target);
        scheduler.SetTarget(-5);
        Assert.Equal(1, scheduler.Target);
        scheduler.SetTarget(3);
        Assert.Equal(3, scheduler.Target);
    }

    [Fact]
    public async Task WaitForIdleCompletesImmediatelyWhenNothingQueued()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 2);
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FaultingJobDoesNotStallTheQueue()
    {
        var scheduler = new AdaptiveScheduler(initialTarget: 1);
        Exception? seen = null;
        scheduler.JobFailed += ex => seen = ex;
        bool secondRan = false;

        scheduler.Enqueue(_ => throw new InvalidOperationException("boom"));
        scheduler.Enqueue(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        await scheduler.WaitForIdleAsync();

        Assert.True(secondRan);
        Assert.IsType<InvalidOperationException>(seen);
    }
}
