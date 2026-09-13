using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// <c>sym</c> ranks its answer against the query itself — exact, then prefix, then substring,
/// source before generated — and cuts <c>--max</c> out of that, because the server's own
/// ordering was measured absent on three real corpora. Proving it needs a symbol set where
/// relevance order and alphabetical order differ, and an arrival order that is neither.
/// </summary>
public class OutputTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    private static SymbolInformation Symbol(
        string name, string file, int line = 1, string? container = null, int column = 1, int kind = 5) =>
        new(name, kind, new Location(
            PathUri.FromPath(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar))),
            new Range(new Position(line - 1, column - 1), new Position(line - 1, column + 3))), container);

    private static SymbolRow Row(SymbolInformation symbol) => new(symbol, symbol.Kind);

    private static DocumentContext Context(string csproj, string? tfm) =>
        new($"3fa8|{csproj}{(tfm is null ? string.Empty : $" (${tfm})")}", csproj, tfm, null);

    /// <summary>
    /// No label lookups: these cases render file URIs, which carry their own path. The
    /// generated and metadata labels are <see cref="PathUriTests"/>'s, where no capture is
    /// needed. <c>Lines</c> answers for whatever is asked, so a renderer that reaches for a
    /// context line gets one rather than an exception.
    /// </summary>
    private static readonly Documents Plain = new(
        _ => Task.FromResult<string[]>(["line one", "line two"]),
        _ => Task.FromResult<string?>(null),
        _ => Task.FromResult<string?>(null));

    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Both streams, because the channel is the thing under test for a non-answer: in text
    /// mode stdout carries the answer and nothing else, so an empty answer has to leave it
    /// empty. A capture of the two folded together cannot see that.
    /// </summary>
    private static async Task<(string Out, string Error)> CaptureBothAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static string[] Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The cap keeps the best matches, not an alphabetical prefix of them and not an arrival
    /// prefix either: answered AbcZed / ZedHelper / Zed, a cap of two keeps the exact match
    /// and the prefix match, and displays them alphabetically.
    /// </summary>
    [Fact]
    public async Task Truncation_happens_in_relevance_order_and_display_in_alphabetical_order()
    {
        var symbols = new[]
        {
            Symbol("AbcZed", "Core/AbcZed.cs"),
            Symbol("ZedHelper", "Core/ZedHelper.cs"),
            Symbol("Zed", "App/Zed.cs"),
        };

        var lines = (await CaptureAsync(() => Output.WriteSymbolsAsync(Root, "Zed", symbols, max: 2, json: false, Plain)))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("Zed ", lines[0], StringComparison.Ordinal);
        Assert.Contains("App/Zed.cs:1:1", lines[0], StringComparison.Ordinal);
        Assert.Contains("ZedHelper", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("AbcZed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncation_says_how_many_were_dropped_and_how_to_see_them()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs"), Symbol("B", "Core/B.cs"), Symbol("C", "Core/C.cs") };

        var text = await CaptureAsync(() => Output.WriteSymbolsAsync(Root, "A", symbols, max: 1, json: false, Plain));

        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_answer_says_so_rather_than_printing_nothing()
    {
        var (stdout, stderr) = await CaptureBothAsync(
            () => Output.WriteSymbolsAsync(Root, "A", [], 50, json: false, Plain));

        Assert.Equal(string.Empty, stdout);
        Assert.Equal("cslq: no results", stderr.Trim());
    }

    /// <summary>
    /// Positions are one-based on the way out and zero-based on the wire, in both renderings.
    /// </summary>
    [Fact]
    public async Task Json_reports_one_based_positions_and_whether_it_truncated()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs", line: 9), Symbol("B", "Core/B.cs") };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "A", symbols, max: 1, json: true, Plain))).RootElement;

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("A", only.GetProperty("name").GetString());
        Assert.Equal("class", only.GetProperty("kind").GetString());
        Assert.Equal("Core/A.cs", only.GetProperty("path").GetString());
        Assert.Equal(9, only.GetProperty("line").GetInt32());
        Assert.Equal(1, only.GetProperty("column").GetInt32());
        // Both label flags, so an agent never has to parse the <generated>/ or <metadata>/
        // prefix off `path` to know what kind of document it is looking at.
        Assert.False(only.GetProperty("generated").GetBoolean());
        Assert.False(only.GetProperty("metadata").GetBoolean());
    }

    /// <summary>
    /// The cut is a relevance cut, not an arrival cut. Roslyn was measured answering a
    /// substring hit ahead of the exact match on three real corpora, so a cap of one taken in
    /// arrival order showed <c>BomUser</c> for the query <c>Use</c> and reported the exact
    /// match only as one of the hits it dropped.
    /// </summary>
    [Fact]
    public async Task An_exact_match_survives_the_cap_over_a_substring_hit_that_arrived_first()
    {
        var symbols = new[] { Symbol("BomUser", "Core/BomUser.cs"), Symbol("Use", "App/Use.cs") };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "Use", symbols, max: 1, json: true, Plain))).RootElement;

        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("Use", only.GetProperty("name").GetString());
        Assert.Equal("App/Use.cs", only.GetProperty("path").GetString());
    }

    /// <summary>
    /// Two hits equally exact are separated by where they live: the corpora answered the
    /// generated copies of a name first, so a cap taken in arrival order kept the copy and
    /// dropped the declaration the caller came for.
    /// </summary>
    [Fact]
    public async Task A_source_hit_survives_the_cap_over_an_equally_exact_generated_one()
    {
        var symbols = new[]
        {
            new SymbolInformation(
                "Stamp", 5, Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234", "Stamp.g.cs"), 3), null),
            Symbol("Stamp", "Core/Stamp.cs"),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "Stamp", symbols, max: 1, json: true, Plain))).RootElement;

        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("Core/Stamp.cs", only.GetProperty("path").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
    }

    /// <summary>
    /// A candidate listing is <c>sym</c>'s rows: the same order — exact name first, then
    /// source before generated, then label, line and column — and a <c>path:line:col</c> a
    /// caller can paste straight back as a target, which the old listing's <c>path:line</c>
    /// could not be.
    /// </summary>
    [Fact]
    public async Task A_candidate_listing_orders_by_relevance_then_rank_then_position()
    {
        var candidates = new[]
        {
            Row(Symbol("AbcZed", "App/AbcZed.cs")),
            Row(new SymbolInformation(
                "Zed", 5, Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234", "Zed.g.cs"), 3), null)),
            Row(Symbol("Zed", "Zzz/Zed.cs", line: 4, column: 12)),
            Row(Symbol("Zed", "App/Zed.cs", line: 2, column: 7)),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Zed", candidates, max: 10, Plain)).Split('\n');

        Assert.Equal(4, lines.Length);
        Assert.EndsWith("App/Zed.cs:2:7", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Zzz/Zed.cs:4:12", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("Zed.g.cs:3:1", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("App/AbcZed.cs:1:1", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("  class  Zed ", lines[0], StringComparison.Ordinal);
    }

    /// <summary>The cap is the one the output rules require, footer included.</summary>
    [Fact]
    public async Task A_candidate_listing_caps_at_max_and_says_how_many_it_dropped()
    {
        var candidates = new[]
        {
            Row(Symbol("Zed", "App/Zed.cs")),
            Row(Symbol("Zed", "Core/Zed.cs")),
            Row(Symbol("Zed", "Web/Zed.cs")),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Zed", candidates, max: 1, Plain)).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.EndsWith("App/Zed.cs:1:1", lines[0], StringComparison.Ordinal);
        Assert.Equal("... 2 more (use --max 3 to see all)", lines[1]);
    }

    /// <summary>
    /// <c>workspace/symbol</c> reports a constructor as a method; the caller that already read
    /// the declaration chain says otherwise, and the listing prints what it was told.
    /// </summary>
    [Fact]
    public async Task A_constructor_renders_as_one_when_the_chain_says_so()
    {
        var candidates = new[]
        {
            new SymbolRow(Symbol("Widget", "Core/Widget.cs", line: 13, column: 12, kind: 6), 9),
            new SymbolRow(Symbol("Widget", "Core/Widget.cs", line: 15, column: 12, kind: 6), 9),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Widget", candidates, max: 10, Plain)).Split('\n');

        Assert.Equal("  constructor  Widget    Core/Widget.cs:13:12", lines[0]);
        Assert.EndsWith("Core/Widget.cs:15:12", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The first non-empty line is the signature and the rest is documentation. Roslyn sends
    /// CRLF regardless of platform and always a trailing newline, so both are normalised away
    /// — otherwise the text form prints a phantom blank row and the JSON carries a stray
    /// <c>\r</c>.
    /// </summary>
    [Fact]
    public void A_hover_splits_into_a_signature_and_the_documentation_after_it()
    {
        var (signature, documentation) = Output.HoverText(
            "void Console.WriteLine(string? value) (+ 19 overloads)\r\n"
            + "Writes the specified string value.\r\n\r\nExceptions:\r\n  IOException\r\n");

        Assert.Equal("void Console.WriteLine(string? value) (+ 19 overloads)", signature);
        Assert.Equal("Writes the specified string value.\n\nExceptions:\n  IOException", documentation);
    }

    /// <summary>An undocumented member is a signature and nothing else, not a blank line.</summary>
    [Fact]
    public void A_hover_with_no_documentation_is_a_signature_alone()
    {
        var (signature, documentation) = Output.HoverText("string Greeter.Greet(string name)\r\n");

        Assert.Equal("string Greeter.Greet(string name)", signature);
        Assert.Equal(string.Empty, documentation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public void A_hover_with_nothing_in_it_yields_nothing(string? value)
    {
        Assert.Equal((string.Empty, string.Empty), Output.HoverText(value));
    }

    /// <summary>
    /// The header is the hover's own range, not the position asked about: the server widens a
    /// column inside an identifier to the whole identifier, which is the better answer.
    /// </summary>
    [Fact]
    public async Task A_hover_prints_its_position_then_the_signature_and_documentation()
    {
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 20), Hover("void C.W(string? v)\nWrites it."),
            50, json: false, Plain));

        Assert.Equal(
            ["App/Program.cs:9:17", "void C.W(string? v)", "Writes it."],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// <c>--max</c> caps the documentation's lines — the one result is never what a cap could
    /// usefully trim — and says so the way every other truncation does.
    /// </summary>
    [Fact]
    public async Task Hover_documentation_is_capped_by_max()
    {
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig\none\ntwo\nthree"),
            max: 1, json: false, documents: Plain));

        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("two", text, StringComparison.Ordinal);
        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hover_json_carries_the_envelope_and_the_split_text()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig\ndoc"),
            50, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.Equal(9, only.GetProperty("line").GetInt32());
        Assert.Equal(17, only.GetProperty("column").GetInt32());
        Assert.Equal("sig", only.GetProperty("signature").GetString());
        Assert.Equal("doc", only.GetProperty("documentation").GetString());
    }

    /// <summary>
    /// A position that resolves to no symbol still has to answer through the envelope rather
    /// than printing an empty one.
    /// </summary>
    [Fact]
    public async Task An_absent_hover_is_an_empty_envelope_and_no_results()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(11, 0), null, 50, json: true, Plain))).RootElement;

        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.Empty(json.GetProperty("results").EnumerateArray().ToList());

        var (stdout, stderr) = await CaptureBothAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(11, 0), null, 50, json: false, Plain));

        Assert.Equal(string.Empty, stdout);
        Assert.Equal("cslq: no results", stderr.Trim());
    }

    /// <summary>
    /// <c>ready</c> printed the literal <c>ready</c> under <c>--json</c> too, which broke the
    /// envelope on the one command every session runs first. Text mode still prints it, so a
    /// shell test stays a string comparison.
    /// </summary>
    [Fact]
    public async Task Ready_honours_the_envelope_under_json_and_stays_one_word_without_it()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteReady(3, [], [], json: true);
            return Task.CompletedTask;
        })).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.True(only.GetProperty("ready").GetBoolean());
        Assert.Equal(3, only.GetProperty("projects").GetInt32());
        Assert.Empty(only.GetProperty("skipped").EnumerateArray().ToList());
        Assert.Empty(only.GetProperty("unprobed").EnumerateArray().ToList());

        var text = await CaptureAsync(() =>
        {
            Output.WriteReady(3, [], [], json: false);
            return Task.CompletedTask;
        });
        Assert.Equal("ready", text.Trim());
    }

    /// <summary>
    /// A project readiness could not probe used to be counted in <c>projects</c> anyway —
    /// fourteen of CommunityToolkit's twenty-six, and a dozen of OrchardCore's. The count is
    /// now the probed ones and the other two classes are named separately, so the three add up
    /// to what the solution yielded and a caller can tell a workspace that was fully checked
    /// from one that was not. Directories, root-relative with forward slashes, because two
    /// projects in a tree share a leaf name often enough.
    /// </summary>
    [Fact]
    public async Task Ready_counts_only_probed_projects_and_names_the_rest()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteReady(2, ["src/Linked", "tests/Linked.Tests"], ["src/TopLevel"], json: true);
            return Task.CompletedTask;
        })).RootElement;

        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal(2, only.GetProperty("projects").GetInt32());
        Assert.Equal(
            ["src/Linked", "tests/Linked.Tests"],
            only.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["src/TopLevel"],
            only.GetProperty("unprobed").EnumerateArray().Select(e => e.GetString()));

        var text = await CaptureAsync(() =>
        {
            Output.WriteReady(2, ["src/Linked"], [], json: false);
            return Task.CompletedTask;
        });
        Assert.Equal("ready", text.Trim());
    }

    [Fact]
    public async Task Project_renders_the_csproj_root_relative_with_its_framework()
    {
        var csproj = Path.Combine(Root, "App", "App.csproj");
        var file = Path.Combine(Root, "App", "Program.cs");

        var text = await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, [Context(csproj, "net10.0")], 50, json: false);
            return Task.CompletedTask;
        });
        Assert.Equal("App/App.csproj  net10.0", text.Trim());

        var json = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, [Context(csproj, "net10.0")], 50, json: true);
            return Task.CompletedTask;
        })).RootElement;
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.Equal("App/App.csproj", only.GetProperty("project").GetString());
        Assert.Equal("net10.0", only.GetProperty("tfm").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
        Assert.False(only.GetProperty("metadata").GetBoolean());
    }


    /// <summary>
    /// A multi-targeted document is compiled several times, so <c>project</c> prints one row
    /// per context in <see cref="Contexts.Order"/>'s order and counts them all. Printing one
    /// of them with <c>count: 1</c> was the bug: it said the file had a single home, and which one
    /// it named changed between runs.
    /// </summary>
    [Fact]
    public async Task Project_prints_one_row_per_context_in_order()
    {
        var csproj = Path.Combine(Root, "Multi", "Multi.csproj");
        var file = Path.Combine(Root, "Multi", "Conditional.cs");
        DocumentContext[] contexts = [Context(csproj, "net10.0"), Context(csproj, "net9.0")];

        var text = await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, contexts, 50, json: false);
            return Task.CompletedTask;
        });
        Assert.Equal(
            ["Multi/Multi.csproj  net10.0", "Multi/Multi.csproj  net9.0"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var json = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, contexts, 50, json: true);
            return Task.CompletedTask;
        })).RootElement;
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        var rows = json.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(["net10.0", "net9.0"], rows.Select(r => r.GetProperty("tfm").GetString()));
        Assert.All(rows, r => Assert.Equal("Multi/Conditional.cs", r.GetProperty("path").GetString()));
    }

    /// <summary>
    /// <c>--max</c> applies to contexts like it does to every other row set: a linked file in
    /// a 16-context solution is the shape that needs it.
    /// </summary>
    [Fact]
    public async Task Project_rows_are_capped_by_max()
    {
        var csproj = Path.Combine(Root, "Multi", "Multi.csproj");
        var text = await CaptureAsync(() =>
        {
            Output.WriteProject(
                Root,
                Path.Combine(Root, "Multi", "Conditional.cs"),
                [Context(csproj, "net10.0"), Context(csproj, "net9.0")],
                max: 1,
                json: false);
            return Task.CompletedTask;
        });

        Assert.Contains("net10.0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("net9.0", text, StringComparison.Ordinal);
        Assert.Contains("... 1 more (use --max 2 to see all)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The answer to a question about a multi-targeted document says which of its contexts
    /// answered, and names the others: the useful next move is asking a different one with
    /// <c>--tfm</c>.
    /// </summary>
    [Fact]
    public async Task An_answer_from_one_of_several_contexts_names_it()
    {
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("Multi/Conditional.cs"), new Position(10, 20), Hover("class Only9"),
            50, json: false, Plain, Note(1)));

        Assert.Equal(
            [
                "Multi/Conditional.cs:9:17",
                "class Only9",
                "answered in net9.0 of 2 contexts: net10.0, net9.0",
            ],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A single-context document is the ordinary case and says nothing: a note on every
    /// answer would train a caller to skip it.
    /// </summary>
    [Fact]
    public async Task A_single_context_answer_says_nothing_about_contexts()
    {
        var only = Context(Path.Combine(Root, "App", "App.csproj"), "net10.0");
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig"),
            50, json: false, Plain, new ContextNote([only], [only], only)));

        Assert.Equal(
            ["App/Program.cs:9:17", "sig"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// An empty answer says every context was tried: a caller who cannot tell that from
    /// "asked the wrong one" are indistinguishable again.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_says_how_many_contexts_were_tried()
    {
        var (stdout, stderr) = await CaptureBothAsync(() => Output.WriteHoverAsync(
            Root, Uri("Multi/Conditional.cs"), new Position(1, 0), null,
            50, json: false, Plain, Note(0)));

        // One line, not a paragraph: the note rides on the `cslq:` line rather than under it.
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(["cslq: no results; tried all 2 contexts: net10.0, net9.0"], Lines(stderr));
    }

    /// <summary>
    /// With <c>--tfm</c> the tried set is the subset, and the note has to say which — an agent
    /// asking "does net9.0 build" must not read a net9.0-only miss as an absence.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_under_tfm_names_the_context_it_asked()
    {
        var csproj = Path.Combine(Root, "Multi", "Multi.csproj");
        DocumentContext[] all = [Context(csproj, "net10.0"), Context(csproj, "net9.0")];

        var (stdout, stderr) = await CaptureBothAsync(() => Output.WriteLocationsAsync(
            Root, [], 50, 1, json: false, Plain, new ContextNote(all, [all[1]], all[1])));

        Assert.Equal(string.Empty, stdout);
        Assert.Equal(
            ["cslq: no results; tried net9.0 of 2 contexts: net10.0, net9.0"], Lines(stderr));
    }

    /// <summary>
    /// The same fact in the envelope rather than a trailing line, so a caller parsing JSON
    /// never has to read prose: which context answered, and how many the document has.
    /// </summary>
    [Fact]
    public async Task Context_bound_json_carries_the_tfm_and_the_context_count()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("Multi/Conditional.cs"), new Position(10, 20), Hover("class Only9"),
            50, json: true, Plain, Note(1)))).RootElement;

        Assert.Equal("net9.0", json.GetProperty("tfm").GetString());
        Assert.Equal(2, json.GetProperty("contexts").GetInt32());
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// And nothing at all for the commands that do not choose a context yet: their envelope
    /// keys are unchanged rather than present and meaningless.
    /// </summary>
    [Fact]
    public async Task An_answer_with_no_note_keeps_the_plain_envelope()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig"),
            50, json: true, Plain))).RootElement;

        Assert.False(json.TryGetProperty("tfm", out _));
        Assert.False(json.TryGetProperty("contexts", out _));
    }


    /// <summary>
    /// An outline of a multi-targeted document is the union of its contexts, and the
    /// declarations that are not in every one of them carry the contexts they are in. A file
    /// whose whole body sat inside one <c>#if</c> answered <c>no symbols</c> at exit 0 before
    /// this — a wrong answer rather than a partial one.
    /// </summary>
    [Fact]
    public async Task An_outline_marks_the_declarations_that_are_not_in_every_context()
    {
        var text = await CaptureAsync(() => Output.WriteOutlineAsync(
            Root,
            Uri("Multi/Conditional.cs"),
            Outline.Merge(
            [
                new OutlineView("net10.0", [Node("Only10", 4)]),
                new OutlineView("net9.0", [Node("Only9", 11)]),
            ]),
            contexts: 2,
            50,
            json: false,
            Plain,
            UnionNote()));

        Assert.Equal(
            [
                "Multi/Conditional.cs",
                "   4 | Only10  [net10.0]",
                "  11 | Only9  [net9.0]",
                "merged from 2 contexts: net10.0, net9.0",
            ],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A hit on a 20,079-character line printed the whole line -- for one result -- which is
    /// the context window the output rules exist to protect. Text mode keeps a window around
    /// the column the row is about, marks each cut end, and leaves the header column alone:
    /// that is what still pastes back as a target.
    /// </summary>
    [Fact]
    public void A_long_line_is_elided_around_the_column_the_row_is_about()
    {
        var line = new string('a', 500) + "NEEDLE" + new string('b', 500);

        var elided = Output.Elide(line, column: 501, budget: 20);

        // Centred: the column sits half a budget in, so the window opens ten characters
        // before the hit and the rest of it runs on past.
        Assert.Equal("…aaaaaaaaaaNEEDLEbbbb…", elided);
        Assert.Equal(22, elided.Length);
    }

    /// <summary>
    /// A context line has no column of its own, so it keeps its head: the start of a
    /// statement is what says what it is.
    /// </summary>
    [Fact]
    public void A_line_with_no_column_keeps_its_head()
    {
        Assert.Equal("aaaaa…", Output.Elide(new string('a', 40), column: null, budget: 5));
    }

    /// <summary>
    /// A window at either end of the line is a whole window, not half of one, and only the
    /// cut end is marked -- so the marker is a reliable "there is more this way".
    /// </summary>
    [Fact]
    public void A_hit_near_an_end_still_gets_a_whole_window()
    {
        var line = new string('a', 100);

        Assert.Equal(new string('a', 10) + "…", Output.Elide(line, column: 1, budget: 10));
        Assert.Equal("…" + new string('a', 10), Output.Elide(line, column: 100, budget: 10));
    }

    /// <summary>
    /// And a line inside the budget is untouched -- no marker, no trim -- which is every line
    /// of hand-written C# and the reason the common case reads exactly as it did.
    /// </summary>
    [Fact]
    public void A_line_inside_the_budget_is_untouched()
    {
        Assert.Equal("short", Output.Elide("short", column: 3));
        Assert.Equal(
            new string('a', Output.LineBudget),
            Output.Elide(new string('a', Output.LineBudget), column: 1));
    }

    /// <summary>
    /// A line several declarations start on used to print once per declaration:
    /// <c>public enum Colour { Red, Green, Blue }</c> came out four times, and a
    /// multi-declarator field twice. Each crowded row prints its own span instead, and its
    /// gutter grows the identifier column — the same column <c>--json</c> reports, so the row
    /// is still a <c>line:col</c> a caller can paste back.
    /// </summary>
    [Fact]
    public async Task Declarations_sharing_a_line_print_their_own_span_and_column()
    {
        const string Source = "public enum Colour { Red, Green, Blue }";
        var text = await CaptureAsync(() => Output.WriteOutlineAsync(
            Root,
            Uri("Core/Kinds.cs"),
            Outline.Merge(
            [
                new OutlineView(string.Empty,
                [
                    Spanning("Colour", 1, 12, 0, Source.Length,
                        Spanning("Red", 1, 21, 21, 24),
                        Spanning("Green", 1, 26, 26, 31),
                        Spanning("Blue", 1, 33, 33, 37)),
                ]),
            ]),
            contexts: 1,
            50,
            json: false,
            new Documents(
                _ => Task.FromResult<string[]>([Source]),
                _ => Task.FromResult<string?>(null),
                _ => Task.FromResult<string?>(null))));

        Assert.Equal(
            [
                "Core/Kinds.cs",
                "  1:13 | public enum Colour { Red, Green, Blue }",
                "  1:22 |   Red",
                "  1:27 |   Green",
                "  1:34 |   Blue",
            ],
            Lines(text));
    }

    /// <summary>
    /// And a document where every declaration has its line to itself keeps the bare line
    /// number and the whole source line, which is the common case and the whole value of an
    /// outline.
    /// </summary>
    [Fact]
    public async Task A_declaration_alone_on_its_line_still_prints_that_whole_line()
    {
        var text = await CaptureAsync(() => Output.WriteOutlineAsync(
            Root,
            Uri("Core/Kinds.cs"),
            Outline.Merge(
            [
                new OutlineView(string.Empty,
                [
                    Spanning("Colour", 2, 12, 0, 39),
                ]),
            ]),
            contexts: 1,
            50,
            json: false,
            new Documents(
                _ => Task.FromResult<string[]>(["namespace Fixture.Core;", "    public enum Colour { Red }"]),
                _ => Task.FromResult<string?>(null),
                _ => Task.FromResult<string?>(null))));

        Assert.Equal(
            ["Core/Kinds.cs", "  2 | public enum Colour { Red }"],
            Lines(text));
    }

    /// <summary>
    /// One context, or a declaration every context compiles: no mark, no note, byte-for-byte
    /// what an outline was before contexts existed.
    /// </summary>
    [Fact]
    public async Task A_single_context_outline_is_unchanged()
    {
        var text = await CaptureAsync(() => Output.WriteOutlineAsync(
            Root,
            Uri("Core/Greeter.cs"),
            Outline.Merge([new OutlineView(string.Empty, [Node("Greeter", 3)])]),
            contexts: 1,
            50,
            json: false,
            Plain));

        Assert.Equal(
            ["Core/Greeter.cs", "  3 | Greeter"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Outline_json_carries_the_context_count_and_a_per_row_tfm()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteOutlineAsync(
            Root,
            Uri("Multi/Conditional.cs"),
            Outline.Merge(
            [
                new OutlineView("net10.0", [Node("Both", 8), Node("Only10", 4)]),
                new OutlineView("net9.0", [Node("Both", 8)]),
            ]),
            contexts: 2,
            50,
            json: true,
            Plain,
            UnionNote()))).RootElement;

        Assert.Equal(2, json.GetProperty("contexts").GetInt32());
        var rows = json.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(["Only10", "Both"], rows.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("net10.0", rows[0].GetProperty("tfm").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("tfm").ValueKind);
    }

    /// <summary>
    /// A diagnostic only one context reports carries that context — the <c>net9.0</c>-only
    /// CS0029, which an unqualified pull reported in 1 run of 4 and otherwise not at all — and
    /// one every context reports carries nothing.
    /// </summary>
    [Fact]
    public async Task A_diagnostic_only_some_contexts_report_names_them()
    {
        var only = new Report(Uri("Multi/TfmError.cs"), Error("CS0029", 10), ["net9.0"], 2);
        var shared = new Report(
            Uri("Multi/TfmError.cs"), Error("IDE0002", 3), ["net10.0", "net9.0"], 2);

        var text = await CaptureAsync(() => Output.WriteDiagnosticsAsync(
            Root, [shared, only], 50, 0, json: false, Plain, UnionNote()));

        Assert.Equal(
            [
                "Multi/TfmError.cs:3:1 error IDE0002: boom",
                "Multi/TfmError.cs:10:1 error CS0029: boom [net9.0]",
                "merged from 2 contexts: net10.0, net9.0",
            ],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Diagnostic_json_carries_the_context_on_the_row()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteDiagnosticsAsync(
            Root,
            [new Report(Uri("Multi/TfmError.cs"), Error("CS0029", 10), ["net9.0"], 2)],
            50,
            0,
            json: true,
            Plain,
            UnionNote()))).RootElement;

        Assert.Equal(2, json.GetProperty("contexts").GetInt32());
        // Not tfm: null. A union has no answering context, and a null field an agent has to
        // interpret is the complaint against an always-null `source`; the envelope omits the key instead.
        Assert.False(json.TryGetProperty("tfm", out _));
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("net9.0", only.GetProperty("tfm").GetString());
    }

    /// <summary>
    /// One position carries several findings — three sat at
    /// <c>ValueTypeExtensions.cs:19:61</c> on CommunityToolkit — and position alone left them in
    /// the order the server sent, which differed between two runs of one command. Severity, code
    /// and message finish the order, so the array a caller parses is the same every run and
    /// <c>--max</c> cuts the same set. Fed here in an order that is none of those.
    /// </summary>
    [Fact]
    public async Task Findings_at_one_position_are_ordered_by_severity_then_code_then_message()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteDiagnosticsAsync(
            Root,
            [
                At("IDE0300", 4, "Collection initialization can be simplified"),
                At("IDE0300", 3, "Slice can be simplified"),
                At("IDE0230", 3, "Use UInt32 overload"),
                At("CS0029", 1, "Cannot implicitly convert"),
                At("IDE0300", 3, "Collection initialization can be simplified"),
            ],
            50,
            0,
            json: true,
            Plain))).RootElement;

        Assert.Equal(
            [
                "error CS0029: Cannot implicitly convert",
                "info IDE0230: Use UInt32 overload",
                "info IDE0300: Collection initialization can be simplified",
                "info IDE0300: Slice can be simplified",
                "hint IDE0300: Collection initialization can be simplified",
            ],
            json.GetProperty("results").EnumerateArray().Select(r =>
                $"{r.GetProperty("severity").GetString()} {r.GetProperty("code").GetString()}: " +
                r.GetProperty("message").GetString()));
    }

    /// <summary>
    /// A single-context document says nothing and marks nothing, which is every document in an
    /// ordinary repository.
    /// </summary>
    [Fact]
    public async Task A_single_context_diagnostic_is_unchanged()
    {
        var text = await CaptureAsync(() => Output.WriteDiagnosticsAsync(
            Root, [new Report(Uri("App/Program.cs"), Error("CS0029", 4), [string.Empty], 1)],
            50, 0, json: false, Plain));

        Assert.Equal(
            ["App/Program.cs:4:1 error CS0029: boom"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A set answer names every context it merged rather than one that answered — and says
    /// <c>merged from</c> rather than <c>tried</c>, because a correct answer is sitting right
    /// above the line and "tried" beside one reads as a failure to rule out.
    /// </summary>
    [Fact]
    public async Task A_union_answer_says_it_merged_the_contexts()
    {
        var text = await CaptureAsync(() => Output.WriteLocationsAsync(
            Root, [Location("Multi/Both.cs", 8)], 50, 0, json: false, Plain, UnionNote()));

        Assert.Contains("merged from 2 contexts: net10.0, net9.0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("answered in", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tried", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>tried</c> is kept for the empty union, where nothing was found and it is the only
    /// true thing to say — and it still names the subset when <c>--tfm</c> narrowed the ask.
    /// </summary>
    [Fact]
    public async Task An_empty_union_still_says_it_tried()
    {
        var (_, stderr) = await CaptureBothAsync(() => Output.WriteLocationsAsync(
            Root, [], 50, 0, json: false, Plain, UnionNote()));

        Assert.Contains("tried all 2 contexts: net10.0, net9.0", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A union narrowed to one context still merged <em>that</em> context, and says so with
    /// the same <c>X of N</c> shape the other notes use.
    /// </summary>
    [Fact]
    public async Task A_union_under_tfm_names_the_subset_it_merged()
    {
        var csproj = Path.Combine(Root, "Multi", "Multi.csproj");
        DocumentContext[] all = [Context(csproj, "net10.0"), Context(csproj, "net9.0")];

        var text = await CaptureAsync(() => Output.WriteLocationsAsync(
            Root,
            [Location("Multi/Conditional.cs", 11)],
            50,
            0,
            json: false,
            Plain,
            new ContextNote(all, [all[1]], Answered: null)));

        Assert.Contains(
            "merged from net9.0 of 2 contexts: net10.0, net9.0", text, StringComparison.Ordinal);
    }

    private static DocumentSymbol Node(string name, int line) =>
        new(
            name,
            name,
            5,
            new Range(new Position(line - 1, 0), new Position(line + 1, 0)),
            new Range(new Position(line - 1, 20), new Position(line - 1, 26)),
            []);

    /// <summary>
    /// A declaration whose extent is given explicitly, on one line: what a crowded row
    /// slices its own text out of. <paramref name="column"/> and the two offsets are
    /// zero-based, like the wire.
    /// </summary>
    private static DocumentSymbol Spanning(
        string name, int line, int column, int from, int to, params DocumentSymbol[] children) =>
        new(
            name,
            name,
            5,
            new Range(new Position(line - 1, from), new Position(line - 1, to)),
            new Range(new Position(line - 1, column), new Position(line - 1, column + name.Length)),
            children);

    private static Diagnostic Error(string code, int line) =>
        new(
            new Range(new Position(line - 1, 0), new Position(line - 1, 4)),
            1,
            JsonDocument.Parse($"\"{code}\"").RootElement,
            "boom");

    /// <summary>One document, one position: only severity, code and message separate these.</summary>
    private static Report At(string code, int severity, string message) =>
        new(
            Uri("Diagnostics/ValueTypeExtensions.cs"),
            new Diagnostic(
                new Range(new Position(18, 60), new Position(18, 64)),
                severity,
                JsonDocument.Parse($"\"{code}\"").RootElement,
                message),
            [string.Empty],
            1);

    private static Location Location(string file, int line) =>
        new(Uri(file), new Range(new Position(line - 1, 20), new Position(line - 1, 24)));

    /// <summary>
    /// The two-context note <c>fixture2/Multi</c> produces: net10.0 first in
    /// <see cref="Contexts.Order"/>, and <paramref name="answered"/> the index that answered,
    /// or null for an answer no context gave.
    /// </summary>
    /// <summary>
    /// The note a set-valued answer carries: every context asked, none of them singled out.
    /// </summary>
    private static ContextNote UnionNote()
    {
        var note = Note(0);
        return note with { Answered = null };
    }

    private static ContextNote Note(int? answered)
    {
        var csproj = Path.Combine(Root, "Multi", "Multi.csproj");
        DocumentContext[] all = [Context(csproj, "net10.0"), Context(csproj, "net9.0")];
        return new ContextNote(all, all, answered is null ? all[0] : all[answered.Value]);
    }

    /// <summary>
    /// A file no project compiles is answered, not errored: it is the reason <c>sym</c> cannot
    /// see the types in it and <c>diag</c> reports nothing for it.
    /// </summary>
    [Fact]
    public async Task A_file_no_project_compiles_says_no_project()
    {
        var (stdout, stderr) = await CaptureBothAsync(() =>
        {
            Output.WriteProject(Root, Path.Combine(Root, "Ambient", "Stray.cs"), [], 50, json: false);
            return Task.CompletedTask;
        });

        Assert.Equal(string.Empty, stdout);
        Assert.Equal("cslq: no project", stderr.Trim());
    }

    /// <summary>
    /// The other half of the rule: <c>--json</c> is honoured on the empty answer too, and the
    /// envelope stays the ordinary one — <c>count: 0</c> is how a machine reads "no results",
    /// so there is nothing for stderr to add. A caller parsing JSON therefore never has to
    /// handle a body that is missing.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_under_json_keeps_the_envelope_and_says_nothing_on_stderr()
    {
        var (stdout, stderr) = await CaptureBothAsync(
            () => Output.WriteLocationsAsync(Root, [], 50, 1, json: true, Plain));

        var json = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Empty(json.GetProperty("results").EnumerateArray().ToList());
        Assert.False(json.TryGetProperty("error", out _));
        Assert.Equal(string.Empty, stderr);
    }

    /// <summary>
    /// A failure under <c>--json</c> is a single object on stdout carrying the same message,
    /// and the human line still goes to stderr so a log reads. <c>error</c> is the
    /// discriminator: an answer envelope never carries it, so one field separates the two
    /// shapes a caller sees at the same exit code.
    /// </summary>
    [Fact]
    public async Task A_failure_under_json_is_an_error_object_on_stdout_and_a_line_on_stderr()
    {
        var (stdout, stderr) = await CaptureBothAsync(() =>
        {
            Output.WriteError("no symbol matched 'NoSuch'", json: true);
            return Task.CompletedTask;
        });

        var json = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("no symbol matched 'NoSuch'", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("count", out _));
        Assert.Equal("cslq: no symbol matched 'NoSuch'", stderr.Trim());
    }

    [Fact]
    public async Task A_failure_without_json_leaves_stdout_empty()
    {
        var (stdout, stderr) = await CaptureBothAsync(() =>
        {
            Output.WriteError("no such file: nope.cs", json: false);
            return Task.CompletedTask;
        });

        Assert.Equal(string.Empty, stdout);
        Assert.Equal("cslq: no such file: nope.cs", stderr.Trim());
    }

    /// <summary>
    /// A source-generated URI for <paramref name="hint"/>, carrying the fields that are
    /// regenerated on every workspace load. Two of these differing only in
    /// <paramref name="authority"/> are what a multi-targeted project answers with: different
    /// URIs, one label. See <see cref="PathUriTests"/> for the shape.
    /// </summary>
    private static string GeneratedUri(
        string authority,
        string hint = "BuildInfo.g.cs",
        string generator = "Fixture.Gen.BuildInfoGenerator") =>
        $"roslyn-source-generated://{authority}/{hint}"
        + $"?documentId={authority}&assemblyName=Fixture.App&assemblyVersion=1.0.0.0"
        + $"&typeName={generator}&hintName={hint}";

    private static Location Loc(string uri, int line, int column = 1) =>
        new(uri, new Range(new Position(line - 1, column - 1), new Position(line - 1, column + 4)));

    /// <summary>
    /// Roslyn answers the declaration of a type with a primary constructor twice — once for
    /// the type, once for the constructor — at byte-identical positions, and the count used to
    /// include the twin. Rows are folded on what they render as plus the range.
    /// </summary>
    [Fact]
    public async Task Two_locations_that_render_identically_are_one_row()
    {
        var twins = new[] { Loc(Uri("App/Square.cs"), 2, 22), Loc(Uri("App/Square.cs"), 2, 22) };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Single(json.GetProperty("results").EnumerateArray().ToList());

        var text = await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: false, Plain));

        Assert.Equal(
            ["App/Square.cs:2:22", "> 2 | line two"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A multi-targeted project answers one generated document once per framework, under URIs
    /// whose authority guid and documentId differ while the label does not. The fold is on the
    /// label, so those collapse; folding on the URI would not have touched them.
    /// </summary>
    [Fact]
    public async Task Generated_twins_that_differ_only_in_the_volatile_uri_fields_fold()
    {
        var twins = new[]
        {
            Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234"), 5),
            Loc(GeneratedUri("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"), 5),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal(
            "<generated>/Fixture.App/Fixture.Gen.BuildInfoGenerator/BuildInfo.g.cs",
            only.GetProperty("path").GetString());
        Assert.True(only.GetProperty("generated").GetBoolean());
    }

    /// <summary>
    /// Enough generated hits will fill the cap on their own, and the source hits are what the
    /// caller came for, so the cap applies to source rows first.
    /// </summary>
    [Fact]
    public async Task Source_rows_come_before_generated_ones_so_the_cap_drops_generated_first()
    {
        var mixed = new[]
        {
            Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234"), 5),
            Loc(Uri("App/Program.cs"), 10),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, mixed, max: 1, 0, json: true, Plain))).RootElement;

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
    }

    private static string Uri(string file) => PathUri.FromPath(
        Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)));

    private static Hover Hover(string value) =>
        new(new MarkupContent("plaintext", value), new Range(new Position(8, 16), new Position(8, 25)));

    [Theory]
    [InlineData(1, "error")]
    [InlineData(2, "warning")]
    public void Severities_render_by_name(int severity, string expected)
    {
        Assert.Equal(expected, Output.Severity(severity));
    }

    /// <summary>
    /// <c>restore</c> reaches no workspace, so its one line names the absolute manifest
    /// directory it restored to rather than anything root-relative, and its JSON goes through
    /// the same <c>{ count, truncated, results }</c> envelope as every other command.
    /// </summary>
    [Fact]
    public async Task Restore_reports_where_it_restored_to()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "tools", "net10.0", "any");

        var text = await CaptureAsync(() =>
        {
            Output.WriteRestored(manifest, pruned: null, json: false);
            return Task.CompletedTask;
        });

        Assert.Equal("restored the pinned language server in " + manifest, text.Trim());

        var json = await CaptureAsync(() =>
        {
            Output.WriteRestored(manifest, pruned: null, json: true);
            return Task.CompletedTask;
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.True(row.GetProperty("restored").GetBoolean());
        Assert.Equal(manifest, row.GetProperty("manifest").GetString());
        Assert.Equal(0, row.GetProperty("removed").GetArrayLength());
    }

    /// <summary>
    /// The prune after a restore is the only thing that ever deletes from the shared packages
    /// folder, so what it removed and what it could not are both named, in text and in JSON.
    /// </summary>
    [Fact]
    public async Task Restore_reports_what_the_prune_removed_and_what_it_could_not()
    {
        var pruned = new Prune.Result(
            "/home/u/.nuget/packages",
            ["roslyn-language-server.linux-x64/5.11.0-2.26311.5", "roslyn-language-server/5.11.0-2.26311.5"],
            [("roslyn-language-server.linux-x64/5.10.0-1.26201.4", "in use.")]);

        var lines = Output.PruneLines(pruned).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "removed 2 other version(s) of the language server from /home/u/.nuget/packages: "
            + "roslyn-language-server.linux-x64/5.11.0-2.26311.5, roslyn-language-server/5.11.0-2.26311.5",
            lines[0]);
        Assert.StartsWith(
            "could not remove roslyn-language-server.linux-x64/5.10.0-1.26201.4 from /home/u/.nuget/packages: in use.",
            lines[1]);

        var json = await CaptureAsync(() =>
        {
            Output.WriteRestored("/m", pruned, json: true);
            return Task.CompletedTask;
        });

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("/home/u/.nuget/packages", row.GetProperty("packages").GetString());
        Assert.Equal(2, row.GetProperty("removed").GetArrayLength());
        Assert.Equal("in use.", row.GetProperty("kept")[0].GetProperty("why").GetString());
    }

    /// <summary>
    /// The two shapes the replay oracle cannot reach, pinned whole rather than probed key by
    /// key: <c>cslq restore</c> really restores and prunes the shared packages folder, so no
    /// capture of it can be replayed, and <c>session stop --json</c> is not a command the gate
    /// runs. Every other <c>--json</c> site is covered by a probe case. The literal is the
    /// point — moving these off anonymous types onto declared records is exactly the change
    /// that renames a key silently, and a caller parsing <c>manifest</c> would never see
    /// <c>Manifest</c> coming.
    /// </summary>
    [Fact]
    public async Task Restore_renders_the_same_envelope_with_and_without_a_prune()
    {
        var bare = await CaptureAsync(() =>
        {
            Output.WriteRestored("/m", null, json: true);
            return Task.CompletedTask;
        });

        Assert.Equal(
            """
            {
              "count": 1,
              "truncated": false,
              "results": [
                {
                  "restored": true,
                  "manifest": "/m",
                  "packages": null,
                  "removed": [],
                  "kept": []
                }
              ]
            }
            """.ReplaceLineEndings("\n"),
            bare.ReplaceLineEndings("\n").TrimEnd('\n'));

        var pruned = await CaptureAsync(() =>
        {
            Output.WriteRestored(
                "/m",
                new Prune.Result("/pkg", ["roslyn-language-server/1.0.0"], [("roslyn-language-server/0.9.0", "in use.")]),
                json: true);
            return Task.CompletedTask;
        });

        Assert.Equal(
            """
            {
              "count": 1,
              "truncated": false,
              "results": [
                {
                  "restored": true,
                  "manifest": "/m",
                  "packages": "/pkg",
                  "removed": [
                    "roslyn-language-server/1.0.0"
                  ],
                  "kept": [
                    {
                      "dir": "roslyn-language-server/0.9.0",
                      "why": "in use."
                    }
                  ]
                }
              ]
            }
            """.ReplaceLineEndings("\n"),
            pruned.ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task Session_stop_renders_the_envelope_either_way()
    {
        var stopped = await CaptureAsync(() =>
        {
            Output.WriteSessionStopped(true, "cslq-abc", json: true);
            return Task.CompletedTask;
        });

        Assert.Equal(
            """
            {
              "count": 1,
              "truncated": false,
              "results": [
                {
                  "stopped": true,
                  "pipe": "cslq-abc"
                }
              ]
            }
            """.ReplaceLineEndings("\n"),
            stopped.ReplaceLineEndings("\n").TrimEnd('\n'));

        using var none = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteSessionStopped(false, "cslq-abc", json: true);
            return Task.CompletedTask;
        }));
        Assert.False(none.RootElement.GetProperty("results")[0].GetProperty("stopped").GetBoolean());
    }

    /// <summary>
    /// A shape nobody registered on <see cref="OutWire"/> throws rather than falling back to
    /// reflection, which is what makes the assembly's AOT compatibility true for a renderer
    /// that has not been written yet. Same guard as <c>LspWireTests</c>'s, one wire over.
    /// </summary>
    [Fact]
    public void An_unregistered_output_shape_is_refused_rather_than_reflected_over()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = OutWire.Default };

        var ex = Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(new Unregistered("x"), typeof(Unregistered), options));

        Assert.Contains(nameof(Unregistered), ex.Message, StringComparison.Ordinal);
    }

    private sealed record Unregistered(string Name);
}
