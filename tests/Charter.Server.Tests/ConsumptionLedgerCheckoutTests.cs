using System;
using System.IO;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The consumption ledger is keyed by the plan's absolute PATH, which stops identifying the reader the moment
/// a checkout is replaced at that path — a fresh clone over a stale one, a rebuilt container mounting the repo
/// at the same point, a wiped-and-recreated worktree (#81).
/// <para>
/// The damage was silent and it was the worst kind: the re-cloned tree brings the same COMMITTED records back
/// with the same ids, so nothing about the inherited ledger looks wrong. It simply belongs to a reader that no
/// longer exists, and every teammate comment the previous checkout consumed is withheld from the new one with
/// no diagnostic. The record is shared, so the person whose comment vanished may be someone else entirely, and
/// they would never learn it was never delivered.
/// </para>
/// </summary>
[Trait("Category", "ConsumptionLedger")]
public class ConsumptionLedgerCheckoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-ledger-checkout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The defect itself: replace the checkout, and the inherited ledger must NOT be honoured.</summary>
    [Fact]
    public void AReplacedCheckoutAtTheSamePath_DiscardsTheInheritedLedger_AndSaysWhy()
    {
        var (consumed, plan) = MakeRepoWithPlan("work");

        var first = ReviewLogLedger.Load(consumed, plan);
        first.MarkConsumed(["cmt_alice_1", "cmt_bob_2"]);
        first.Save();
        Assert.Equal(2, ReviewLogLedger.Load(consumed, plan).Count);

        // The re-clone: same absolute path, same plan, brand-new working tree. Only `.git` changes, which is
        // precisely why repository identity cannot detect this — the remote, the commits and the record ids
        // all come back identical.
        ReplaceCheckout(Path.GetDirectoryName(plan)!);

        var afterReclone = ReviewLogLedger.Load(consumed, plan);

        Assert.Equal(0, afterReclone.Count);
        Assert.False(afterReclone.HasConsumedAll(["cmt_alice_1"]),
            "a comment the PREVIOUS checkout consumed must be delivered to this one");
        Assert.NotNull(afterReclone.ResetReason);
        Assert.Contains("DIFFERENT checkout", afterReclone.ResetReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same checkout must keep its ledger. Re-delivering every comment on every poll would be its own
    /// defect, so the fix has to be narrow: the marker is stable across loads within one working tree.
    /// </summary>
    [Fact]
    public void TheSameCheckoutKeepsItsLedger_AndReportsNoReset()
    {
        var (consumed, plan) = MakeRepoWithPlan("work");

        var first = ReviewLogLedger.Load(consumed, plan);
        first.MarkConsumed(["cmt_alice_1"]);
        first.Save();

        var second = ReviewLogLedger.Load(consumed, plan);

        Assert.Equal(1, second.Count);
        Assert.True(second.HasConsumedAll(["cmt_alice_1"]));
        Assert.Null(second.ResetReason);
    }

    /// <summary>
    /// A plan outside any git repository — the solo reviewer — must behave exactly as it did before #81. There
    /// is no checkout to replace, so there is nothing to detect, and inventing a requirement here would be new
    /// setup the design explicitly refuses (plan-03 §5.0).
    /// </summary>
    [Fact]
    public void APlanOutsideAnyRepository_IsUnaffected()
    {
        var consumed = Path.Combine(_root, "state", "consumed");
        Directory.CreateDirectory(consumed);
        var loose = Path.Combine(_root, "loose");
        Directory.CreateDirectory(loose);
        var plan = Path.Combine(loose, "solo.charter.md");
        File.WriteAllText(plan, "# Solo\n");

        var first = ReviewLogLedger.Load(consumed, plan);
        first.MarkConsumed(["cmt_1"]);
        first.Save();

        var second = ReviewLogLedger.Load(consumed, plan);

        Assert.Equal(1, second.Count);
        Assert.Null(second.ResetReason);
    }

    /// <summary>
    /// A ledger written BEFORE this field existed carries no checkout id. Absence means "cannot tell", and
    /// cannot-tell must never discard a valid ledger — otherwise upgrading Charter would re-deliver every
    /// comment every reviewer had already seen.
    /// </summary>
    [Fact]
    public void ALedgerWithNoCheckoutId_IsHonoured_NotDiscarded()
    {
        var (consumed, plan) = MakeRepoWithPlan("work");
        var ledgerPath = ReviewLogLedger.PathForPlan(consumed, plan);
        Directory.CreateDirectory(consumed);

        // Exactly the pre-#81 on-disk shape.
        File.WriteAllText(ledgerPath,
            "{\"schema\":1,\"planPath\":" + System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(plan))
            + ",\"consumed\":[\"cmt_legacy\"]}");

        var loaded = ReviewLogLedger.Load(consumed, plan);

        Assert.Equal(1, loaded.Count);
        Assert.True(loaded.HasConsumedAll(["cmt_legacy"]));
        Assert.Null(loaded.ResetReason);
    }

    /// <summary>The marker lives inside <c>.git/</c>, which is what makes it clone-scoped by construction.</summary>
    [Fact]
    public void TheCheckoutMarkerLivesInsideGit_SoGitCanNeverCarryItToAClone()
    {
        var (_, plan) = MakeRepoWithPlan("work");

        var id = CheckoutIdentity.ForPlan(plan);

        Assert.NotNull(id);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(plan)!, ".git", "charter-checkout")));
        Assert.Equal(id, CheckoutIdentity.ForPlan(plan));
    }

    private (string Consumed, string Plan) MakeRepoWithPlan(string name)
    {
        var consumed = Path.Combine(_root, "state", "consumed");
        Directory.CreateDirectory(consumed);

        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var plan = Path.Combine(repo, "plan.charter.md");
        File.WriteAllText(plan, "# Plan\n");
        return (consumed, plan);
    }

    /// <summary>Wipe and recreate the working tree at the same path — the re-clone this issue is about.</summary>
    private static void ReplaceCheckout(string repo)
    {
        Directory.Delete(repo, recursive: true);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "plan.charter.md"), "# Plan\n");
    }
}
