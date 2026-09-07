using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The four answers <c>mtga-pbp stop</c> can give, each as a test rather than a
/// manual run.
/// </summary>
/// <remarks>
/// A listener created here stands in for a running watch. None of these touches the
/// default port: a real watch may be on it wherever the suite runs.
/// </remarks>
[Platform("Win")]
public class StopCommandTests
{
    private static int FreshPort() => Random.Shared.Next(40000, 60000);

    private static (int Code, string Out, string Err) Run(string? portArg, TimeSpan? patience = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = StopCommand.Run(portArg, patience ?? TimeSpan.FromSeconds(5), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Test]
    public void A_port_that_is_not_a_number_is_a_usage_error()
    {
        var (code, _, err) = Run("eight");
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("usage: mtga-pbp stop [port]"));
    }

    [Test]
    public void No_watch_on_the_port_says_so()
    {
        var port = FreshPort();
        var (code, _, err) = Run(port.ToString());
        Assert.That(code, Is.EqualTo(1));
        Assert.That(err, Does.Contain($"no watch is running on port {port}"));
    }

    [Test]
    public void A_watch_that_goes_away_is_reported_stopped()
    {
        var port = FreshPort();
        var listener = StopSignal.Listen(port);
        // The watch: once fired, it finishes its poll and exits.
        var watch = Task.Run(() =>
        {
            listener.Wait(TimeSpan.FromSeconds(5));
            listener.Dispose();
        });

        try
        {
            var (code, output, _) = Run(port.ToString());
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output.Trim(), Is.EqualTo("stopped."));
        }
        finally { watch.Wait(); }
    }

    [Test]
    public void A_watch_that_is_slow_to_leave_is_not_called_stopped()
    {
        var port = FreshPort();
        using var listener = StopSignal.Listen(port);   // never let go within the patience
        var (code, output, _) = Run(port.ToString(), patience: TimeSpan.FromMilliseconds(300));
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("still finishing"));
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True, "the request was delivered");
    }

    [Test]
    public void The_default_port_is_the_watch_default()
    {
        Assert.That(StopCommand.DefaultPort, Is.EqualTo(8787));
    }
}
