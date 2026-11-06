using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

internal static class WirelessReplacementTests
{
    private const string OldSerial = "SERIAL-OLD";
    private const string OldMac = "4c6f-d608-0400";
    private const string NewMac = "4c6f-de4b-0500";
    private static readonly P2B5SiteScopeDecision Full = P2B5SiteScopeDecision.Global;

    public static void Run()
    {
        ReplacementEvidenceCannotInheritPhysicalHistory();
        IncompleteReplacementCannotBorrowAnOlderAddress();
        SimultaneousSerialOrMacConflictKeepsIndependentSources();
        Console.WriteLine("WIRELESS_REPLACEMENT_TESTS=3/3 PASS");
    }

    private static void ReplacementEvidenceCannotInheritPhysicalHistory()
    {
        // Reusing a serial or a MAC is as unsafe as reusing the display name/IP.
        foreach (var (serial, mac) in new[] { ("SERIAL-NEW", NewMac), (OldSerial, NewMac), ("SERIAL-NEW", OldMac) })
        {
            using var fixture = new ReplacementFixture();
            fixture.Import(0, 1000, OldSerial, OldMac, "R/M");
            fixture.Import(0, 2000, OldSerial, OldMac, "R/B");
            fixture.Import(0, 3000, OldSerial, OldMac, "I");
            var oldSnapshot = fixture.Read.GetSnapshot(Full);
            var oldResource = oldSnapshot.ApResources.Single();
            var oldRuntime = fixture.RuntimeFor(oldResource);
            var oldRow = fixture.ResourceRow(oldResource.Id);
            var oldManifestId = "wireless-collection:" + oldSnapshot.SourceRevision;
            var oldManifest = JsonSerializer.Serialize(fixture.Catalog.GetWirelessCollectionManifest(oldManifestId, Full));
            var originalOutputs = fixture.Facts().ToDictionary(item => item.Id, item => item.PayloadJson, StringComparer.Ordinal);
            Assert(oldRuntime.Current?.NormalizedState == "OFFLINE" && oldRuntime.PreviousEffective?.NormalizedState == "RUNNING_BACKUP" &&
                oldRuntime.Recent10?.Count == 2, "F10 fixture must start with effective history that a replacement could incorrectly inherit");

            fixture.Import(0, 4000, serial, mac, "R/M");
            var firstReplacement = fixture.Read.GetSnapshot(Full).ApResources.Single();
            Assert(firstReplacement.Id != oldResource.Id && firstReplacement.IdentityStatus == "CONFIRMED" &&
                firstReplacement.DisplayName == oldResource.DisplayName && firstReplacement.FirstSeenMs == 4000,
                "SC-11: complete changed serial/MAC evidence creates a separate resource despite the reused AP name/group/IP");
            var firstRuntime = fixture.Read.GetRuntimeState(firstReplacement.Id, Full, nowMs: 7000)!;
            Assert(firstRuntime.Current?.CapturedMs == 4000 && firstRuntime.PreviousEffective is null && firstRuntime.Recent10?.Count == 0 &&
                firstRuntime.Sources.Single().PreviousEffective is null && firstRuntime.Sources.Single().Recent10.Count == 0,
                "replacement Current starts at its own first evidence; neither resource nor source history inherits the old AP transitions");

            fixture.Import(0, 5000, serial, mac, "R/B");
            fixture.Import(0, 6000, serial, mac, "R/B");
            var replacement = fixture.Read.GetSnapshot(Full).ApResources.Single();
            var runtime = fixture.Read.GetRuntimeState(replacement.Id, Full, nowMs: 7000)!;
            var source = runtime.Sources.Single();
            Assert(replacement.Id == firstReplacement.Id && replacement.Id != oldResource.Id &&
                runtime.Current?.CapturedMs == 6000 && runtime.PreviousEffective?.CapturedMs == 4000 && runtime.Recent10?.Count == 1 &&
                runtime.Recent10.Single().CapturedMs == 5000 && source.Current?.CapturedMs == 6000 &&
                source.PreviousEffective?.CapturedMs == 4000 && source.Recent10.Count == 1 && source.Recent10.Single().CapturedMs == 5000,
                "replacement history records its own effective change and suppresses its unchanged refresh");
            var replacementRefs = fixture.Facts().Where(item => item.CapturedMs >= 4000).Select(item => "fact:" + item.Id).ToHashSet(StringComparer.Ordinal);
            Assert(runtime.Current!.Sources.Concat(runtime.PreviousEffective!.Sources).Concat(runtime.Recent10!.SelectMany(item => item.Sources))
                .All(item => item.SourceAcDeviceId == fixture.AcIds[0] && item.SourceSiteId == "local" && replacementRefs.Contains(item.RawManifestRef)) &&
                replacementRefs.Contains(source.Current!.RawManifestRef) && replacementRefs.Contains(source.PreviousEffective!.RawManifestRef) &&
                source.Recent10.All(item => replacementRefs.Contains(item.RawManifestRef)),
                "aggregate/source Current, PreviousEffective and Recent10 must retain only the replacement's own exact raw fact provenance");
            Assert(fixture.Read.GetRuntimeState(oldResource.Id, Full, nowMs: 7000) is null &&
                JsonSerializer.Serialize(fixture.RuntimeFor(oldResource)) == JsonSerializer.Serialize(oldRuntime),
                "an absent old resource is not served as the replacement; its retained exact evidence reconstructs the unchanged old history");
            Assert(fixture.ResourceRow(oldResource.Id) == oldRow &&
                JsonSerializer.Serialize(fixture.Catalog.GetWirelessCollectionManifest(oldManifestId, Full)) == oldManifest,
                "replacement publication must not overwrite the old resource identity, observation links or collection source detail");
            Assert(originalOutputs.All(pair => fixture.Facts().Single(item => item.Id == pair.Key).PayloadJson == pair.Value),
                "original command outputs remain immutable and addressable after replacement");
            AssertSourceDetail(fixture, replacement);
            Assert(fixture.Scalar("SELECT COUNT(*) FROM wireless_ap_resources") == "2" &&
                fixture.Scalar("SELECT COUNT(*) FROM wireless_ap_observations WHERE resource_id=$id AND relation_status!='OBSERVED'", ("$id", oldResource.Id)) == "0",
                "retained old evidence is not deleted or assigned an inferred ENDED/replacement lifecycle");

            var restarted = new Catalog(fixture.Settings);
            restarted.Initialize();
            var restartedRead = new WirelessReadModelService(restarted);
            Assert(restartedRead.GetSnapshot(Full).ApResources.Single().Id == replacement.Id &&
                JsonSerializer.Serialize(restartedRead.GetRuntimeState(replacement.Id, Full, nowMs: 7000)) == JsonSerializer.Serialize(runtime) &&
                fixture.ResourceRow(oldResource.Id) == oldRow,
                "restart must retain the separate opaque identity and reconstruct only replacement history without mutating the old identity");
        }
    }

    private static void IncompleteReplacementCannotBorrowAnOlderAddress()
    {
        using var fixture = new ReplacementFixture();
        fixture.Import(0, 1000, OldSerial, OldMac, "R/M");
        fixture.Import(0, 2000, OldSerial, OldMac, "R/B");
        var oldResource = fixture.Read.GetSnapshot(Full).ApResources.Single();
        var oldRow = fixture.ResourceRow(oldResource.Id);
        fixture.Import(0, 3000, "SERIAL-NEW", null, "I");
        var candidate = fixture.Read.GetSnapshot(Full).ApResources.Single();
        var runtime = fixture.Read.GetRuntimeState(candidate.Id, Full, nowMs: 7000)!;
        Assert(candidate.Id != oldResource.Id && candidate.IdentityStatus == "CANDIDATE" && candidate.NormalizedMacSet.Count == 0 &&
            candidate.Observations.Single().CapturedMs == 3000 && candidate.Observations.Single().RawState == "I",
            "SC-08/11: a replacement without same-revision address evidence remains a new candidate; older address rows cannot fill the gap");
        Assert(runtime.NormalizedState == "UNKNOWN" && runtime.Current is null && runtime.PreviousEffective is null &&
            (runtime.Recent10?.Count ?? 0) == 0 && runtime.Sources.Count == 0 && runtime.Warnings.Contains("AP_IDENTITY_NOT_CONFIRMED"),
            "an incomplete replacement's raw I cannot inherit the previous AP runtime or prove physical offline");
        AssertSourceDetail(fixture, candidate);

        fixture.Import(0, 4000, "SERIAL-NEW", NewMac, "R/M");
        var complete = fixture.Read.GetSnapshot(Full).ApResources.Single();
        var completeRuntime = fixture.Read.GetRuntimeState(complete.Id, Full, nowMs: 7000)!;
        Assert(complete.Id != oldResource.Id && complete.Id != candidate.Id && complete.IdentityStatus == "CONFIRMED" &&
            completeRuntime.Current?.CapturedMs == 4000 && completeRuntime.PreviousEffective is null && completeRuntime.Recent10?.Count == 0 &&
            completeRuntime.Sources.Single().HistoryStatus == "PARTIAL_EVIDENCE",
            "complete later evidence creates its own exact resource; missing-MAC candidate rows remain disclosed incomplete evidence rather than fabricated history");
        Assert(fixture.ResourceRow(oldResource.Id) == oldRow && fixture.Scalar("SELECT identity_status FROM wireless_ap_resources WHERE id=$id", ("$id", candidate.Id)) == "CANDIDATE",
            "confirmation evidence does not overwrite old identity or create an automatic candidate/replacement authority link");
        AssertSourceDetail(fixture, complete);
    }

    private static void SimultaneousSerialOrMacConflictKeepsIndependentSources()
    {
        foreach (var (serial, mac) in new[] { (OldSerial, NewMac), ("SERIAL-NEW", OldMac) })
        {
            using var fixture = new ReplacementFixture(2);
            fixture.Import(0, 1000, OldSerial, OldMac, "R/M");
            fixture.Import(0, 2000, OldSerial, OldMac, "R/B");
            var oldResource = fixture.Read.GetSnapshot(Full).ApResources.Single();
            var oldRuntime = fixture.RuntimeFor(oldResource);
            fixture.Import(1, 3000, serial, mac, "I");
            var snapshot = fixture.Read.GetSnapshot(Full);
            Assert(snapshot.ApResources.Count == 2 && snapshot.ApResources.All(item => item.IdentityStatus == "CONFLICT" &&
                item.ConflictReason == "SERIAL_MAC_EVIDENCE_CONFLICT" && item.Observations.Count == 1) &&
                snapshot.ApResources.Any(item => item.Id == oldResource.Id),
                "SC-09/12: simultaneous reused serial or MAC retains two resources and independent source evidence, with an explicit conflict");
            foreach (var resource in snapshot.ApResources)
            {
                var runtime = fixture.Read.GetRuntimeState(resource.Id, Full, nowMs: 7000)!;
                Assert(runtime.NormalizedState == "CONFLICT" && runtime.Current is null && runtime.PreviousEffective is null &&
                    (runtime.Recent10?.Count ?? 0) == 0 && runtime.Sources.Count == 0,
                    "identity conflict cannot receive either AP's effective runtime history");
                AssertSourceDetail(fixture, resource);
            }
            Assert(JsonSerializer.Serialize(fixture.RuntimeFor(oldResource)) == JsonSerializer.Serialize(oldRuntime),
                "conflicting newer source evidence cannot alter the previously confirmed AP's retained exact history");
        }
    }

    private static void AssertSourceDetail(ReplacementFixture fixture, WirelessApResourceView resource)
    {
        var snapshot = fixture.Read.GetSnapshot(Full);
        var observation = resource.Observations.Single();
        var fact = fixture.Facts().Single(item => "fact:" + item.Id == observation.RawManifestRef);
        Assert(fact.CapturedMs == observation.CapturedMs && fact.Id == observation.SourceRevision &&
            fact.PayloadJson.Contains(resource.SerialEvidence!, StringComparison.Ordinal) &&
            observation.SourceAcDeviceId == fixture.AcIds[Array.IndexOf(fixture.ProfileIds, fact.ProfileId)] &&
            observation.SourceSiteId == "local" && observation.ResourceId == resource.Id,
            "source observation must bind its own physical identity, source AC/Site, raw command output and exact revision");
        var detail = fixture.Catalog.GetWirelessCollectionManifest("wireless-collection:" + snapshot.SourceRevision, Full, observation.SourceAcDeviceId)!;
        Assert(detail.Items.Count == 1 && detail.Items.Single().ObservationId == observation.Id &&
            detail.Items.Single().RawManifestRef == observation.RawManifestRef && detail.Items.Single().SourceRevision == fact.Id &&
            detail.Items.Single().CapturedMs == fact.CapturedMs && detail.Items.Single().SourceSiteId == "local",
            "filtered collection source detail must keep the same observation/raw/revision tuple without another AP/source's evidence");
        Assert(fixture.Catalog.GetWirelessCollectionManifest(detail.Manifest.Id, P2B5SiteScopeDecision.Denied("revoked")) is null &&
            fixture.Read.GetRuntimeState(resource.Id, P2B5SiteScopeDecision.Denied("revoked"), nowMs: 7000) is null,
            "replacement/candidate/conflict evidence and source detail remain protected by current Site claims");
    }

    private sealed class ReplacementFixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        private readonly string _root;
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public WirelessReadModelService Read { get; }
        public SqliteConnection Db { get; }
        public string[] ProfileIds { get; }
        public string[] AcIds { get; }

        public ReplacementFixture(int sources = 1)
        {
            _root = Path.Combine(_tempRoot, "next-wireless-replacement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Settings = new() { DataRoot = _root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = System.Net.IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
            Read = new(Catalog);
            ProfileIds = new string[sources];
            AcIds = new string[sources];
            for (var i = 0; i < sources; i++)
            {
                var profileId = Guid.NewGuid().ToString("N");
                var address = "192.0.2." + (10 + i);
                var pin = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');
                Catalog.CreateH3cDeviceProfile(new(profileId, address, 22, "reader", "FIXTURE_REFERENCE", pin, true, 0, 0));
                var device = Catalog.QueryManagedDeviceInventory(search: address).Items.Single();
                Catalog.UpdateDeviceInventory(device.Id, new(device.Id, "Fixture AC " + i, address, "H3C", "WX", "AC", "local", null, "fixture", true, profileId), "fixture");
                ProfileIds[i] = profileId;
                AcIds[i] = device.Id;
            }
            Db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "catalog.db"), Pooling = false }.ToString());
            Db.Open();
        }

        public void Import(int source, long captured, string serial, string? mac, string state)
        {
            var outputs = new Dictionary<string, string>
            {
                [H3cCommandPolicy.DisplayVersion] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt")),
                [H3cCommandPolicy.DisplayWlanApAll] = $"<AC>display wlan ap all\nAP-REUSED 1 {state} WA6320 {serial} group-reused 1 day\n<AC>"
            };
            if (mac is not null) outputs[H3cCommandPolicy.DisplayWlanApAllAddress] = $"<AC>display wlan ap all address\nAP-REUSED 192.0.2.201 {mac}\n<AC>";
            Catalog.ImportH3cOfflineReplay(ProfileIds[source], outputs, captured);
        }

        public IReadOnlyList<H3cFactSnapshot> Facts() => ProfileIds.SelectMany(id =>
            Catalog.QueryH3cFacts(id, "command.display.wlan.ap.all", history: true, scope: Full, requireCapturedScope: true)).ToArray();

        public WirelessRuntimeStateView RuntimeFor(WirelessApResourceView resource) => new WirelessRuntimeStateProjection().Project(resource,
            Catalog.QueryWirelessRuntimeHistory(resource, Full, 4096), new("FULL", "GLOBAL"), 7000, new());

        public string? ResourceRow(string id) => Scalar("SELECT serial_evidence||'|'||normalized_mac_set_json||'|'||source_observation_ids_json||'|'||first_seen_ms||'|'||last_seen_ms||'|'||projection_revision FROM wireless_ap_resources WHERE id=$id", ("$id", id));

        public string? Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = Db.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return command.ExecuteScalar()?.ToString();
        }

        public void Dispose()
        {
            Db.Dispose();
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(_root).StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("replacement fixture cleanup escaped its temporary root");
            Directory.Delete(_root, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
