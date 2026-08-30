using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RebuildGateTests
{
    [Test]
    public void A_background_rebuild_does_not_make_the_caller_wait()
    {
        var gate = new RebuildGate();
        using var release = new ManualResetEventSlim(false);
        var finished = false;

        var task = gate.RunInBackground(() => { release.Wait(); finished = true; });

        // The call has already returned while the rebuild is still blocked — the
        // point of the whole class: the star answers first, the site repaints after.
        Assert.That(finished, Is.False);

        release.Set();
        Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(finished, Is.True);
    }

    [Test]
    public void Rebuilds_never_run_on_top_of_each_other()
    {
        var gate = new RebuildGate();
        var running = 0;
        var overlapped = false;
        var tasks = new List<Task>();

        void Rebuild()
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            Thread.Sleep(10);
            Interlocked.Decrement(ref running);
        }

        // Mixed deliberately: the favorite handler queues from request threads while
        // the poll loop calls in from the main thread, and the gate has to hold for
        // any combination of the two.
        for (var i = 0; i < 8; i++) tasks.Add(gate.RunInBackground(Rebuild));
        tasks.Add(Task.Run(() => gate.Run(Rebuild)));

        Assert.That(Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(overlapped, Is.False, "two rebuilds held the gate at once");
    }

    [Test]
    public void A_rebuild_that_throws_does_not_jam_the_gate()
    {
        var gate = new RebuildGate();

        var task = gate.RunInBackground(() => throw new InvalidOperationException("boom"));
        Assert.That(() => task.Wait(TimeSpan.FromSeconds(5)), Throws.TypeOf<AggregateException>());

        // Would never return if the failed rebuild had kept the gate.
        var ran = false;
        gate.Run(() => ran = true);
        Assert.That(ran, Is.True);
    }

    [Test]
    public void A_foreground_rebuild_waits_for_one_already_in_flight()
    {
        var gate = new RebuildGate();
        using var firstStarted = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var firstDone = false;

        var background = gate.RunInBackground(() =>
        {
            firstStarted.Set();
            release.Wait();
            firstDone = true;
        });

        Assert.That(firstStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);

        // The assertion inside is the proof of order: the foreground rebuild can
        // only enter the gate after the background one has left it.
        var foreground = Task.Run(() => gate.Run(() => Assert.That(firstDone, Is.True)));

        release.Set();
        Assert.That(Task.WaitAll(new[] { background, foreground }, TimeSpan.FromSeconds(5)), Is.True);
    }
}
