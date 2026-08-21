using System.Reflection;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The drift guard (Architecture B §2.2, invariant 3). The <c>charter-format</c> skill is the single format
/// source of truth cited by BOTH the drafting agent (to WRITE blocks) and the Guardrails breakdown session
/// (to INTERPRET them). This test BINDS that skill to the code so the two can never silently diverge: it
/// asserts the skill's catalog enumerates exactly the reconciled directive <see cref="BlockKind"/> set
/// (custom-html IN; file-tree / annotated-code struck as vaporware), the skill documents every
/// <see cref="QuestionSpec"/> field (including the new <c>answer</c>), and the skill's declared
/// <c>format-version</c> matches the version this test pins.
///
/// Making the format version part of the bound surface is what enforces "any catalog change bumps the
/// version": adding or removing a <see cref="BlockKind"/> fails the enum assertion, forcing an edit here, and
/// the pinned <see cref="ExpectedFormatVersion"/> must be bumped in lockstep with the skill's frontmatter.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","CharterFormatDrift")].
/// </summary>
[Trait("Category", "CharterFormatDrift")]
public class CharterFormatDriftTests
{
    /// <summary>The charter-format version this slice ships — now single-sourced from
    /// <see cref="CharterFormat.Version"/> in Charter.Core (this test references it rather than re-pinning a
    /// literal, so the skill frontmatter, the code constant, and this assertion can never drift). Bump the code
    /// constant and the skill frontmatter together whenever the catalog changes — that lockstep is the drift
    /// guard.</summary>
    private const int ExpectedFormatVersion = CharterFormat.Version;

    // The reconciled catalog: every BlockKind that is NOT a plain-CommonMark primitive maps to its :::directive
    // token. This is the test-side SSOT the skill is bound against; a new directive kind in code with no entry
    // here fails the coverage assertion below.
    private static readonly IReadOnlyDictionary<BlockKind, string> DirectiveTokens = new Dictionary<BlockKind, string>
    {
        [BlockKind.Note] = "note",
        [BlockKind.Warn] = "warn",
        [BlockKind.Comparison] = "comparison",
        [BlockKind.Diagram] = "diagram",
        [BlockKind.Diff] = "diff",
        [BlockKind.Question] = "question",
        [BlockKind.CustomHtml] = "custom-html",
    };

    private static readonly IReadOnlySet<BlockKind> PrimitiveKinds = new HashSet<BlockKind>
    {
        BlockKind.Prose, BlockKind.Heading, BlockKind.List, BlockKind.Table, BlockKind.Code,
    };

    // Kinds that are NOT catalog directives and therefore carry no catalog binding: the CommonMark primitives
    // plus BlockKind.Unknown — the else-branch fallback an unrecognized :::foo routes to (Charter #22). Unknown
    // is deliberately excluded from the catalog surface: it has no directive token and appears in no catalog row.
    private static readonly IReadOnlySet<BlockKind> NonCatalogKinds =
        new HashSet<BlockKind>(PrimitiveKinds) { BlockKind.Unknown };

    // Directives struck as vaporware — they have no renderer and must not appear as catalog entries.
    private static readonly string[] StruckDirectives = { "file-tree", "annotated-code" };

    [Fact]
    public void BlockKindEnum_IsExactlyTheReconciledSet_CustomHtmlIn_VaporwareOut()
    {
        var actual = Enum.GetNames<BlockKind>().OrderBy(n => n, StringComparer.Ordinal);
        var expected = new[]
        {
            "Prose", "Heading", "List", "Table", "Code",
            "Note", "Warn", "Diagram", "Comparison", "Question", "Diff", "CustomHtml",
            // Unknown is the else-branch fallback for an unrecognized :::foo (Charter #22) — part of the enum,
            // but NOT a catalog directive (asserted below).
            "Unknown",
        }.OrderBy(n => n, StringComparer.Ordinal);

        // Any BlockKind added or removed fails here — forcing an edit to this test AND (per the lockstep rule)
        // a bump of both the skill frontmatter and ExpectedFormatVersion whenever the change is a CATALOG change.
        Assert.Equal(expected, actual);

        // The promotion and the strike, stated explicitly.
        Assert.Contains(BlockKind.CustomHtml, Enum.GetValues<BlockKind>());
        Assert.DoesNotContain("FileTree", Enum.GetNames<BlockKind>());
        Assert.DoesNotContain("AnnotatedCode", Enum.GetNames<BlockKind>());

        // Unknown exists in the enum but is NOT a catalog member: it carries no directive-token binding, so the
        // catalog surface the skill is bound to stays exactly the reconciled directive set.
        Assert.Contains(BlockKind.Unknown, Enum.GetValues<BlockKind>());
        Assert.DoesNotContain(BlockKind.Unknown, DirectiveTokens.Keys);
    }

    [Fact]
    public void EveryDirectiveBlockKind_HasACatalogBinding_CoveredBySkill()
    {
        // Every non-primitive BlockKind must have a token binding in this test's reconciled map, and the skill
        // catalog must list that :::token. A directive kind with no binding here is an unbound catalog surface.
        foreach (var kind in Enum.GetValues<BlockKind>())
        {
            if (NonCatalogKinds.Contains(kind))
            {
                continue;
            }

            Assert.True(
                DirectiveTokens.ContainsKey(kind),
                $"BlockKind.{kind} has no charter-format catalog binding — add it to the skill + this drift test + bump the format version.");
        }
    }

    [Fact]
    public void SkillCatalog_ListsEveryReconciledDirective_AndStrikesTheVaporware()
    {
        var tableRows = CatalogTableRows(ReadSkill());

        // Every kept directive appears as a :::token in a catalog TABLE ROW.
        foreach (var token in DirectiveTokens.Values)
        {
            Assert.True(
                tableRows.Any(row => row.Contains(":::" + token, StringComparison.Ordinal)),
                $":::{token} is missing from the charter-format catalog table.");
        }

        // The struck directives appear in NO catalog table row (they may only be named in the strike prose).
        foreach (var struck in StruckDirectives)
        {
            Assert.DoesNotContain(tableRows, row => row.Contains(struck, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Skill_ExplicitlyStrikes_FileTreeAndAnnotatedCode()
    {
        var text = ReadSkill();

        // Documented as struck (named, so a reader knows they were considered and removed) — but, per the test
        // above, NOT in any catalog row.
        Assert.Contains(":::file-tree", text, StringComparison.Ordinal);
        Assert.Contains(":::annotated-code", text, StringComparison.Ordinal);
        Assert.Contains("no", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skill_DocumentsEveryQuestionSpecField_IncludingAnswer()
    {
        var text = ReadSkill();

        // Bind the QuestionSpec public field surface to the skill: each field name is documented (as a backtick
        // token), so a new/renamed field can't ship without the skill being updated.
        foreach (var field in QuestionSpecFieldTokens())
        {
            Assert.True(
                text.Contains("`" + field + "`", StringComparison.Ordinal),
                $"charter-format does not document the QuestionSpec field '{field}'.");
        }

        // The new open/resolved marker specifically must be documented.
        Assert.Contains("`answer`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_DocumentsBothAcceptedBodyForms_ForDiagramAndDiff()
    {
        // Charter #48/C8. The renderer has accepted BOTH a raw body and a fenced ```mermaid / ```diff body
        // since Charter #40, but the catalog said only "Mermaid source as the body" — and plan-breakdown is
        // told this skill is AUTHORITATIVE, so an interpreter following it alone expected raw Mermaid. The
        // skill must show both forms, or the format SSOT under-specifies what the code accepts.
        var text = ReadSkill();

        Assert.Contains("Fenced body", text, StringComparison.Ordinal);
        Assert.Contains("Raw body", text, StringComparison.Ordinal);
        Assert.Contains("```mermaid", text, StringComparison.Ordinal);
        Assert.Contains("```diff", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_DocumentsTheUnknownDirectiveBodyPreservationRule()
    {
        // Charter #48/C5. The interop rule — an unknown directive's body is parsed through as prose context and
        // NEVER silently dropped — used to live only in Guardrails' skill, so Charter's own format SSOT did not
        // state the rule its renderer/handoff were violating.
        var text = ReadSkill();

        Assert.Contains("never silently dropped", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skill_DeclaresTheBoundFormatVersion_WithinAValidRange()
    {
        var frontMatter = FrontMatter(ReadSkill());

        var version = FrontMatterInt(frontMatter, "format-version");
        var min = FrontMatterInt(frontMatter, "format-min");

        // The version is part of the bound surface — pinned to the single code source so a catalog change
        // without a bump of CharterFormat.Version (and the skill frontmatter) fails.
        Assert.Equal(ExpectedFormatVersion, version);
        // format-min is likewise bound to the single code source (CharterFormat.MinVersion).
        Assert.Equal(CharterFormat.MinVersion, min);
        // A coherent [format-min, format-version] range.
        Assert.True(min <= version, $"format-min ({min}) must be <= format-version ({version}).");
        Assert.True(min >= 1, "format-min must be a positive integer.");
    }

    /// <summary>
    /// The BODY's stated version range must equal the frontmatter's — and therefore the code's (Charter #155).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frontmatter is already pinned to <see cref="CharterFormat.Version"/> by the test above, so it
    /// cannot drift from the code. The body's prose was a separate, hand-written copy of the same two numbers
    /// and was bound to nothing — bump the format and the sentence a reader actually reads would quietly
    /// become false.
    /// </para>
    /// <para>
    /// That matters more here than the usual documentation-drift argument, because the body is the half a
    /// CONSUMER can read. Frontmatter is discovery metadata and does not reliably reach an agent's context, so
    /// Guardrails' <c>plan-breakdown</c> — which cites this skill as the authority for interpreting a
    /// <c>.charter.md</c> — would be checking the plan's marker against a number no test defended.
    /// </para>
    /// </remarks>
    [Fact]
    public void Skill_BodyStatesTheSameVersionRangeAsItsFrontmatter()
    {
        var text = ReadSkill();
        var frontMatter = FrontMatter(text);

        var version = FrontMatterInt(frontMatter, "format-version");
        var min = FrontMatterInt(frontMatter, "format-min");
        // Everything past the closing frontmatter fence — the half a reader (and an agent's context)
        // actually gets. Searched from offset 4 so the OPENING fence is not mistaken for the close.
        var closeFence = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(closeFence > 0, "charter-format/SKILL.md has no closing frontmatter fence.");
        var body = text[(closeFence + 4)..];

        Assert.Contains($"`format-version: {version}`", body, StringComparison.Ordinal);
        Assert.Contains($"`format-min: {min}`", body, StringComparison.Ordinal);

        // ...and the numbers really are the code's, so all three are one fact rather than three copies.
        Assert.Equal(CharterFormat.Version, version);
        Assert.Equal(CharterFormat.MinVersion, min);
    }

    /// <summary>
    /// A consumer that finds the pairing wrong must be told the remedy is a CHARTER command — re-installing
    /// its own tool's skills will not help, and a Guardrails-side agent has no reason to think of it.
    /// </summary>
    [Fact]
    public void Skill_TellsAConsumerTheRemedyAndWhoseRepoItIsIn()
    {
        var text = ReadSkill();

        Assert.Contains("charter skills install --force", text, StringComparison.Ordinal);
        // Installing alone is not enough: the loaded copy does not change when the file on disk does (#152).
        Assert.Contains("restarting your session", text, StringComparison.OrdinalIgnoreCase);
        // And it must name the consumer this is written for, or it reads as advice to Charter's own agents.
        Assert.Contains("plan-breakdown", text, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>The lowercase wire tokens of every public instance field of <see cref="QuestionSpec"/>.</summary>
    private static IEnumerable<string> QuestionSpecFieldTokens()
        => typeof(QuestionSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant());

    /// <summary>The catalog table rows (lines starting with '|') between the catalog heading and the next '##'.</summary>
    private static IReadOnlyList<string> CatalogTableRows(string skill)
    {
        var lines = skill.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rows = new List<string>();
        var inCatalog = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                // Enter on the block-catalog heading; leave on the next section heading.
                inCatalog = line.Contains("block catalog", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inCatalog && line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                rows.Add(line);
            }
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    /// <summary>The plain-YAML frontmatter block (between the first two <c>---</c> lines) as raw text.</summary>
    private static string FrontMatter(string skill)
    {
        var lines = skill.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.True(lines.Length > 0 && lines[0].Trim() == "---", "charter-format SKILL.md must open with YAML frontmatter.");

        var body = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return string.Join("\n", body);
            }

            body.Add(lines[i]);
        }

        Assert.Fail("charter-format frontmatter has no closing '---'.");
        return string.Empty;
    }

    /// <summary>Read integer key <paramref name="key"/> from a plain <c>key: value</c> frontmatter block.</summary>
    private static int FrontMatterInt(string frontMatter, string key)
    {
        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                var value = trimmed[(key.Length + 1)..].Trim();
                Assert.True(int.TryParse(value, out var parsed), $"frontmatter '{key}' must be an integer, was '{value}'.");
                return parsed;
            }
        }

        Assert.Fail($"charter-format frontmatter is missing '{key}'.");
        return 0;
    }

    /// <summary>Read the charter-format SKILL.md, located by walking up to the repo root (Charter.sln).</summary>
    private static string ReadSkill()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Charter.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repo root (Charter.sln) from the test base directory.");

        var path = Path.Combine(dir!.FullName, "skills", "charter-format", "SKILL.md");
        Assert.True(File.Exists(path), $"charter-format skill not found at {path}.");
        return File.ReadAllText(path);
    }
}
