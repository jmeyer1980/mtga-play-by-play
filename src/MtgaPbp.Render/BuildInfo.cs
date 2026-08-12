using System.Reflection;

namespace MtgaPbp.Render;

/// <summary>
/// Which build of the tool wrote a report.
/// </summary>
/// <remarks>
/// Every page and every markdown export carries this, because a report is a file that
/// outlives the run that made it and gets read long after. A <c>watch</c> left running
/// from an older release will happily rewrite the entire report with old code, and
/// without a stamp the output and the source disagree with nothing on the page to say
/// why — which is exactly how a morning got spent proving a fixed bug was still broken.
/// <para>
/// Read from this assembly rather than the entry assembly so it is the same answer in a
/// test, in the CLI and in anything else that renders a page.
/// </para>
/// </remarks>
public static class BuildInfo
{
    /// <summary>
    /// The version, with the commit it was built from where the build knew it —
    /// <c>0.3.0+a1b2c3d4</c>. Two builds of one release differ only in the commit, and
    /// that is the half that identifies a working copy.
    /// </summary>
    /// <remarks>
    /// Settable for the same reason the display timezone is: a golden file that baked in
    /// the commit would fail on every commit, including the ones that changed nothing it
    /// covers, and a test that has to be regenerated constantly stops being read.
    /// </remarks>
    public static string Version { get; set; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The stamp as it reads on a page.</summary>
    public static string Line => $"Written by mtga-pbp {Version}";
}
