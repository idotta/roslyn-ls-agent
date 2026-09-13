namespace Cslq.Tests;

/// <summary>
/// What <c>LspClient.WaitReadyAsync</c> asks for on a second call against one attach. Nothing
/// in <c>probes/</c> can guard this: the fixture is four projects, where the round it skips
/// costs single-digit milliseconds, so a timing leg would pass just as happily on a cache that
/// never fired. The decision is a pure predicate instead, and this is where it is pinned.
/// </summary>
public class ReadyProofTests
{
    /// <summary>
    /// The whole point: the second request does not re-ask what the first one proved. On a
    /// 214-project repository that round is the request — measured 2026-09-13, it is ~9.5 s of
    /// a ~9.5 s warm <c>outline</c>.
    /// </summary>
    [Fact]
    public void A_proved_sentinel_is_not_probed_again()
    {
        var project = Project("Web");
        var proved = Prove(project);

        Assert.Empty(LspClient.Unproved([project], proved));
    }

    /// <summary>
    /// Only what actually resolved is remembered. A project still loading is asked again on
    /// every call, with the deadline and grace <c>WaitReadyAsync</c> has always applied —
    /// caching a negative is what would turn a slow load into an answer that is quietly
    /// incomplete.
    /// </summary>
    [Fact]
    public void A_sentinel_that_has_not_resolved_is_probed_every_call()
    {
        var web = Project("Web");
        var api = Project("Api");
        var proved = Prove(web);

        Assert.Equal(["Api"], LspClient.Unproved([web, api], proved).Select(Name));
    }

    /// <summary>
    /// The reason this is a set of per-project keys and not a single ready flag.
    /// <c>Program.Sentinels</c> recomputes the list from disk on every request, so a project
    /// added after the session started appears in a later call — and must be probed. A flag
    /// would skip it, and every query would then answer without it at exit 0.
    /// </summary>
    [Fact]
    public void A_project_added_after_the_session_started_is_probed()
    {
        var web = Project("Web");
        var proved = Prove(web);
        var added = Project("Reports");

        Assert.Equal(["Reports"], LspClient.Unproved([web, added], proved).Select(Name));
    }

    /// <summary>
    /// A project that contributes no candidate is not waited on — there is nothing to ask for
    /// — exactly as before the cache. <c>WaitReadyAsync</c> still names it on the failure path.
    /// </summary>
    [Fact]
    public void A_project_with_no_candidate_is_never_pending()
    {
        var bare = new Sentinel(Path.Combine(Root, "Bare"), [], []);

        Assert.Empty(LspClient.Unproved([bare], new HashSet<string>()));
    }

    /// <summary>
    /// An explicit <c>--sentinel</c> is not a project: its directory is the root, so keying on
    /// the directory alone would have one prove the other. Proving the explicit probe here must
    /// leave the project sentinel pending even though both name the same candidate.
    /// </summary>
    [Fact]
    public void An_explicit_sentinel_does_not_prove_a_project_that_shares_its_name()
    {
        var project = new Sentinel(Path.Combine(Root, "Explicit"), ["Greeter"], []);
        var explicitProbe = new Sentinel(Root, ["Greeter"], [], Explicit: true);
        var proved = Prove(explicitProbe);

        Assert.Equal(["Explicit"], LspClient.Unproved([project, explicitProbe], proved).Select(Name));
    }

    /// <summary>
    /// And the other direction, with the confusable pair the prefixes exist for: a project
    /// directory named after the explicit probe's candidate is still its own key.
    /// </summary>
    [Fact]
    public void A_project_does_not_prove_an_explicit_sentinel()
    {
        var project = Project("Greeter");
        var explicitProbe = new Sentinel(Root, ["Greeter"], [], Explicit: true);
        var proved = Prove(project);

        Assert.Equal(
            ["explicit"],
            LspClient.Unproved([project, explicitProbe], proved).Select(s => s.Explicit ? "explicit" : Name(s)));
    }

    /// <summary>
    /// The key is the directory itself, not the string it arrived as: a trailing separator or a
    /// <c>..</c> segment names the same project and must not cost a second round.
    /// </summary>
    [Fact]
    public void The_same_directory_spelled_differently_is_one_proof()
    {
        var proved = Prove(Project("Web"));
        var again = new Sentinel(
            Path.Combine(Root, "Api", "..", "Web") + Path.DirectorySeparatorChar, ["Program"], []);

        Assert.Empty(LspClient.Unproved([again], proved));
    }

    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "cslq-ready-proof");

    private static Sentinel Project(string name) =>
        new(Path.Combine(Root, name), ["Program"], []);

    private static HashSet<string> Prove(params Sentinel[] sentinels) =>
        [.. sentinels.Select(LspClient.ProofKey)];

    private static string Name(Sentinel sentinel) => Path.GetFileName(sentinel.Directory);
}
