using System.Text;
using System.Text.Json.Nodes;
using Charter.Core;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Builds a REAL <c>charter handoff --manifest</c> pair in a temp directory and drives <c>charter verify</c>
/// over it (Charter #192).
/// </summary>
/// <remarks>
/// <para>
/// The pair is always produced by the actual binary, never hand-written. A verifier tested against fixtures a
/// test author typed is a verifier tested against that author's belief about the format — which is the defect
/// class the shared key constants and the shared <c>DeriveManifestPath</c> exist to close. Tampering happens
/// AFTER the real write, so every test starts from a pair Charter itself vouches for.
/// </para>
/// </remarks>
internal static class VerifyFixture
{
    /// <summary>A plan whose one question is answered inline: the gate is clean, so <c>needsHuman</c> is false
    /// and any exit 2 a test sees comes from a join, not from the escalation clause.</summary>
    public const string AnsweredPlan =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\nProse.\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"Postgres\"]}\n:::\n";

    /// <summary>A plan with one answered and one OPEN question, so the manifest records
    /// <c>gate.needsHuman: true</c> while every join still holds.</summary>
    public const string OpenQuestionPlan =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"Postgres\"]}\n:::\n\n:::question\n"
        + "{\"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Redis\", \"in-memory\"]}\n:::\n";

    public static string PlanPath(string dir) => Path.Combine(dir, "plan.charter.md");

    public static string HandoffPath(string dir) => Path.Combine(dir, "plan.md");

    public static string ManifestPath(string dir) => Path.Combine(dir, "plan.manifest.json");

    /// <summary>Write <paramref name="plan"/> and run the real <c>charter handoff -o plan.md --manifest</c>.</summary>
    public static (int ExitCode, string StdOut, string StdErr) Build(
        string dir, string plan, params string[] extraArgs)
    {
        File.WriteAllText(PlanPath(dir), plan);

        var args = new List<string> { "handoff", PlanPath(dir), "-o", HandoffPath(dir), "--manifest" };
        args.AddRange(extraArgs);

        var result = CharterCliRunner.Run(args.ToArray());
        Assert.True(File.Exists(ManifestPath(dir)), "the fixture's handoff run must have written a manifest.");
        return result;
    }

    public static (int ExitCode, string StdOut, string StdErr) Verify(string dir)
        => CharterCliRunner.Run("verify", HandoffPath(dir));

    public static string ReadHandoff(string dir) => File.ReadAllText(HandoffPath(dir));

    /// <summary>The plan hash Charter recorded for this fixture — read from the manifest, never recomputed, so
    /// a test edits the value the producer actually wrote.</summary>
    public static string PlanSha256(string dir)
        => (string)Manifest(dir)["planSha256"]!;

    public static JsonObject Manifest(string dir)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(ManifestPath(dir)))!;

    /// <summary>Rewrite the handoff's TEXT, preserving Charter's own encoding (UTF-8, no byte order mark).</summary>
    public static void EditHandoff(string dir, Func<string, string> edit)
    {
        var before = ReadHandoff(dir);
        var after = edit(before);
        Assert.NotEqual(before, after);
        File.WriteAllText(HandoffPath(dir), after, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Write the handoff's exact BYTES — for the encoding and line-ending cases, where the text is not
    /// what is being changed.</summary>
    public static void WriteHandoffBytes(string dir, byte[] bytes)
        => File.WriteAllBytes(HandoffPath(dir), bytes);

    public static void EditManifest(string dir, Action<JsonObject> edit)
    {
        var manifest = Manifest(dir);
        edit(manifest);
        File.WriteAllText(ManifestPath(dir), manifest.ToJsonString());
    }
}
