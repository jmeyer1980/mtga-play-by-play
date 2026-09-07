using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The named event that lets <c>mtga-pbp stop</c> end a <c>watch</c> it cannot see.
/// </summary>
/// <remarks>
/// Windows-only by nature: named events are a kernel feature the other platforms lack,
/// and the tool only ever runs where Arena does. The port in each test is only a name;
/// a random one keeps parallel tests out of each other's way, and none of them is 8787,
/// where a real watch may be listening on the machine running the suite.
/// </remarks>
[Platform("Win")]
public class StopSignalTests
{
    private static int FreshPort() => Random.Shared.Next(40000, 60000);

    [Test]
    public void Nothing_listening_means_nothing_to_fire()
    {
        var port = FreshPort();
        Assert.That(StopSignal.IsListening(port), Is.False);
        Assert.That(StopSignal.Fire(port), Is.False);
    }

    [Test]
    public void Fire_from_outside_ends_the_wait()
    {
        var port = FreshPort();
        using var listener = StopSignal.Listen(port);
        Assert.That(listener.IsNamed, Is.True);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.False, "nothing has fired yet");

        Assert.That(StopSignal.Fire(port), Is.True);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True);
    }

    [Test]
    public void Set_from_inside_ends_the_wait_too()
    {
        // The Ctrl+C path, and later the icon's Quit.
        using var listener = StopSignal.Listen(FreshPort());
        listener.Set();
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True);
    }

    [Test]
    public void The_name_is_per_port()
    {
        int a = FreshPort(), b = a + 1;
        using var listener = StopSignal.Listen(a);
        Assert.That(StopSignal.Fire(b), Is.False);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.False);
        Assert.That(StopSignal.Fire(a), Is.True);
    }

    [Test]
    public void Listening_lasts_exactly_as_long_as_the_listener()
    {
        var port = FreshPort();
        Assert.That(StopSignal.IsListening(port), Is.False);
        var listener = StopSignal.Listen(port);
        Assert.That(StopSignal.IsListening(port), Is.True);
        listener.Dispose();
        Assert.That(StopSignal.IsListening(port), Is.False);
    }

    [Test]
    public void The_name_says_what_it_is_for()
    {
        Assert.That(StopSignal.NameFor(8787), Is.EqualTo(@"Local\mtga-pbp-stop-8787"));
    }

    [Test]
    public void A_request_is_visible_to_whoever_asks_next()
    {
        // The Ctrl+C handler's rule: the first press is a request, a second one an order.
        using var listener = StopSignal.Listen(FreshPort());
        Assert.That(listener.AlreadyRequested, Is.False);
        listener.Set();
        Assert.That(listener.AlreadyRequested, Is.True);
    }
}
