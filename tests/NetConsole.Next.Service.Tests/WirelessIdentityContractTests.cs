using NetConsole.Next.Service;

internal static class WirelessIdentityContractTests
{
    public static void Run()
    {
        CandidateWithIncompleteEvidenceCannotBeConfirmed();
        ConflictCanOnlyBeRejectedAndKeepsEvidenceRefs();
        IdentityRevisionIgnoresRuntimeStateButChangesWithEvidence();
        Console.WriteLine("WIRELESS_IDENTITY_CONTRACT_TESTS=3/3 PASS");
    }

    private static void CandidateWithIncompleteEvidenceCannotBeConfirmed()
    {
        var resource = Resource("CANDIDATE", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" },
            Observation("obs-1", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" }, "raw-1", "rev-1"),
            Observation("obs-2", "SERIAL-1", Array.Empty<string>(), "raw-2", "rev-2"));
        var review = WirelessIdentityContract.Describe(resource);
        Assert(!review.CanConfirm && review.CanReject && review.DecisionBlockReason == "INCOMPLETE_EXACT_EVIDENCE",
            "candidate identity with an incomplete observation must not be confirmable");
        Assert(review.EvidenceRefs.SequenceEqual(new[] { "raw-1", "raw-2" }),
            "identity review must expose only deduplicated raw evidence references");
    }

    private static void ConflictCanOnlyBeRejectedAndKeepsEvidenceRefs()
    {
        var resource = Resource("CONFLICT", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" },
            Observation("obs-1", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" }, "raw-1", "rev-1")) with
        { ConflictReason = "SERIAL_MAC_EVIDENCE_CONFLICT" };
        var review = WirelessIdentityContract.Describe(resource);
        Assert(!review.CanConfirm && review.CanReject && review.DecisionBlockReason == "SERIAL_MAC_EVIDENCE_CONFLICT" &&
            review.ConflictReason == "SERIAL_MAC_EVIDENCE_CONFLICT",
            "conflicting identity evidence must fail closed and allow only rejection");
    }

    private static void IdentityRevisionIgnoresRuntimeStateButChangesWithEvidence()
    {
        var observation = Observation("obs-1", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" }, "raw-1", "rev-1");
        var resource = Resource("CONFIRMED", "SERIAL-1", new[] { "4C:6F:D6:08:04:00" }, observation);
        var refreshed = resource with { Observations = new[] { observation with { RawState = "R/B", NormalizedState = "RUNNING_BACKUP" } } };
        var changed = resource with { Observations = new[] { observation with { SourceRevision = "rev-2" } } };
        Assert(resource.IdentityRevision == refreshed.IdentityRevision,
            "runtime state refresh must not invalidate an identity revision");
        Assert(resource.IdentityRevision != changed.IdentityRevision,
            "a new source revision must invalidate the identity revision");
    }

    private static WirelessApResourceView Resource(string status, string serial, IReadOnlyList<string> macs,
        params WirelessApObservationView[] observations) => new("resource-1", "AP-1", status, serial, macs,
        observations.Select(item => item.Id).ToArray(), new[] { "domain-1" }, "FULL", 1, 2, "source-1", "projection-1", null,
        observations, null);

    private static WirelessApObservationView Observation(string id, string? serial, IReadOnlyList<string> macs,
        string raw, string revision) => new(id, "resource-1", "ac-1", "domain-1", "site-1", "AP-1", serial, macs,
        "WA", "group", "R/M", "RUNNING_MASTER", "HIGH", 1, 1, raw, "parser-1", revision, "OBSERVED");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
