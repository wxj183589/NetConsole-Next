using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class P2B1ContractTests
{
    public static void Run()
    {
        LoadsSyntheticFixture();
        VerifiesAddressIdentityAndClaims();
        VerifiesLegacyManagementIpCompatibility();
        VerifiesNetworkDomainScope();
        VerifiesVlanValidityAndConflicts();
        VerifiesAuthorityAndEffectiveProjection();
        VerifiesErrorsAndFeatureGate();
        VerifiesReadOnlySchemaV13Preflight();
        Console.WriteLine("PASS: P2B-1 address/domain/VLAN/provenance/error contracts and read-only Schema v13 preflight");
    }

    private static void LoadsSyntheticFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "p2b1", "p2b1_contract_fixture.json");
        Assert(File.Exists(path), "P2B-1 synthetic fixture must be copied to the test output directory");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert(document.RootElement.GetProperty("contractVersion").GetString() == "p2b1-1",
            "P2B-1 fixture version must be explicit");
        Assert(document.RootElement.GetProperty("sourceFacts").GetArrayLength() == 2,
            "P2B-1 fixture must include source facts");
    }

    private static void VerifiesAddressIdentityAndClaims()
    {
        var address = P2BAddressNormalization.Normalize("192.0.2.10", 24);
        var mapped = P2BAddressNormalization.Normalize("::ffff:192.0.2.10", 24);
        Assert(address == mapped && address.ClaimKey == mapped.ClaimKey,
            "IPv4-mapped IPv6 must normalize to the IPv4 claim namespace");

        var v6A = P2BAddressNormalization.Normalize("2001:0DB8:0:0:0:0:0:1", 64);
        var v6B = P2BAddressNormalization.Normalize("2001:db8::1", 64);
        Assert(v6A == v6B && v6A.AddressFamily == P2BAddressFamily.IPV6,
            "IPv6 textual variants must normalize to one address identity");
        AssertThrows<ArgumentOutOfRangeException>(() => P2BAddressNormalization.Normalize("192.0.2.10", 33),
            "IPv4 prefix length must be bounded");

        var sameDeviceObserved = Fact("device-a", "domain-a", address, P2BSourceKind.OBSERVED, "ac-a", 1, 1000, 2000, "obs-a");
        var sameDevicePlanned = Fact("device-a", "domain-a", address, P2BSourceKind.PLANNED, "plan-a", 1, 1000, 2000, "plan-a");
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, sameDevicePlanned).Kind == P2BConflictKind.SOURCE_FACT_COEXISTENCE,
            "same device multi-source facts must coexist");

        var repeatedImport = Fact("device-a", "domain-a", address, P2BSourceKind.IMPORTED, "import-a", 1, 1000, 2000, "same-fingerprint");
        Assert(P2BAddressConflictRules.Compare(repeatedImport, repeatedImport with { }).Kind == P2BConflictKind.SOURCE_FACT_DUPLICATE,
            "repeated import of the same source revision must be idempotent");

        var historicalRevision = repeatedImport with
        {
            Source = repeatedImport.Source with { Revision = 2 },
            Validity = new P2BValidityInterval(2000, 3000),
            ImportFingerprint = "new-revision"
        };
        Assert(P2BAddressConflictRules.Compare(repeatedImport, historicalRevision).Kind == P2BConflictKind.SOURCE_FACT_COEXISTENCE,
            "historical source revisions must be retained rather than treated as duplicates");

        var sameDomainOtherDevice = Fact("device-b", "domain-a", address, P2BSourceKind.PLANNED, "plan-b", 1, 1000, 2000);
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, sameDomainOtherDevice).Kind == P2BConflictKind.ADDRESS_ALREADY_CLAIMED,
            "different devices cannot claim an active address in one Network Domain");

        var differentDomain = sameDomainOtherDevice with { NetworkDomainId = "domain-b" };
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, differentDomain).Kind == P2BConflictKind.CROSS_DOMAIN_REUSE_ALLOWED,
            "explicitly different Network Domains may reuse an address");

        var nonOverlapping = sameDomainOtherDevice with { Validity = new P2BValidityInterval(2000, 3000) };
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, nonOverlapping).Kind == P2BConflictKind.HISTORICAL_REUSE_ALLOWED,
            "non-overlapping address validity intervals may be reused");

        var differentPrefix = sameDomainOtherDevice with { Address = P2BAddressNormalization.Normalize("192.0.2.10", 25) };
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, differentPrefix).Kind == P2BConflictKind.ADDRESS_ALREADY_CLAIMED,
            "prefix length must not bypass an address claim conflict");

        var unresolved = sameDomainOtherDevice with { NetworkDomainId = null };
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, unresolved).Kind == P2BConflictKind.NETWORK_DOMAIN_NOT_RESOLVED,
            "an address without an explicit domain must remain unresolved");
        var sameRevisionDifferentAddress = sameDeviceObserved with
        {
            Address = P2BAddressNormalization.Normalize("192.0.2.11", 24)
        };
        Assert(P2BAddressConflictRules.Compare(sameDeviceObserved, sameRevisionDifferentAddress).Kind == P2BConflictKind.SOURCE_CONFLICT,
            "one source revision must not publish two different address facts");
    }

    private static void VerifiesLegacyManagementIpCompatibility()
    {
        var records = new[]
        {
            new P2BLegacyDeviceRecord("legacy-a", "192.0.2.10", 24, "site-a", "profile-a", 1000),
            new P2BLegacyDeviceRecord("legacy-b", "192.0.2.10", 24, "site-b", "profile-b", 1001),
            new P2BLegacyDeviceRecord("legacy-c", "192.0.2.11", 24, "site-a", null, 1002)
        };
        var plan = P2BLegacyManagementIpRules.Plan(records);
        Assert(plan.Records.Single(x => x.DeviceId == "legacy-c").State == P2BLegacyAddressState.LEGACY_ADDRESS_PENDING_DOMAIN,
            "a unique legacy management IP must enter pending-domain staging");
        Assert(plan.Records.Where(x => x.DeviceId is "legacy-a" or "legacy-b").All(x => x.State == P2BLegacyAddressState.LEGACY_ADDRESS_CONFLICT),
            "duplicate legacy IP claims across sites must require domain review");
        Assert(plan.IsIdempotentWith(P2BLegacyManagementIpRules.Plan(records)),
            "legacy planning must be deterministic and idempotent");

        var resolved = P2BLegacyManagementIpRules.ResolveDomain(plan.Records[0], "domain-a");
        Assert(resolved.State == P2BLegacyAddressState.LEGACY_ADDRESS_RESOLVED &&
               resolved.NetworkDomainId == "domain-a" && resolved.DeviceId == plan.Records[0].DeviceId &&
               resolved.ManagementIp == plan.Records[0].ManagementIp &&
               resolved.TerminalProfileId == plan.Records[0].TerminalProfileId,
            "domain resolution must preserve legacy device identity, IP, and terminal profile link");
        Assert(P2BLegacyManagementIpRules.SourceReference("legacy-a") == "legacy-management-ip:legacy-a",
            "legacy compatibility reads must retain a deterministic source reference");
    }

    private static void VerifiesNetworkDomainScope()
    {
        var scope = new P2BNetworkDomainScope(
            new[] { "site-a", "site-b" }, new[] { "station-a", "station-b" }, new[] { "line-1" }, new[] { "section-1" },
            P2BScopeResolution.RESOLVED);
        var domain = new P2BNetworkDomainContract("domain-shared", "Shared", P2BNetworkDomainKind.SHARED_SERVICE,
            scope, null, "vrf-shared", P2BLifecycleState.ACTIVE, 1, Source(P2BSourceKind.PLANNED, "domain-plan", 1));
        domain.Validate();
        var json = JsonSerializer.Serialize(domain, JsonOptions());
        Assert(json.Contains("\"resolution\":\"RESOLVED\"", StringComparison.Ordinal),
            "Network Domain scope serialization must use stable enum names");
        Assert(scope.IsCrossSite && scope.StationReferences.Count == 2 &&
               scope.IsVisibleTo(new HashSet<string>(new[] { "site-a", "site-b" }), false) &&
               !scope.IsVisibleTo(new HashSet<string>(new[] { "site-a" }), false) &&
               P2BNetworkDomainRules.CanAssociateSite(domain, "site-b"),
            "cross-site domain visibility must require all referenced sites and permit explicit station membership");

        AssertThrows<ArgumentException>(() => P2BNetworkDomainRules.ValidateNoParentCycle(
            new Dictionary<string, string?> { ["domain-a"] = "domain-b", ["domain-b"] = "domain-a" }),
            "Network Domain parent cycles must be rejected");
        P2BNetworkDomainRules.ValidateNoParentCycle(new Dictionary<string, string?> { ["domain-a"] = "domain-b", ["domain-b"] = null });
        AssertThrows<ArgumentException>(() => new P2BNetworkDomainScope(
            new[] { "site-a", "site-a" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), P2BScopeResolution.RESOLVED).Validate(),
            "duplicate scope references must be rejected");
    }

    private static void VerifiesVlanValidityAndConflicts()
    {
        var adjacentLeft = VlanClaim("domain-a", 120, 1000, 2000, P2BSourceKind.PLANNED);
        var adjacentRight = VlanClaim("domain-a", 120, 2000, 3000, P2BSourceKind.PLANNED);
        Assert(P2BVlanConflictRules.Compare(adjacentLeft, adjacentRight) == P2BConflictKind.HISTORICAL_REUSE_ALLOWED,
            "adjacent half-open VLAN intervals must not overlap");
        Assert(P2BVlanConflictRules.Compare(adjacentLeft, VlanClaim("domain-a", 120, 1500, 2500, P2BSourceKind.PLANNED)) == P2BConflictKind.VLAN_VALIDITY_OVERLAP,
            "same-domain same-VLAN overlapping planned claims must conflict");
        Assert(P2BVlanConflictRules.Compare(adjacentLeft, VlanClaim("domain-b", 120, 1500, 2500, P2BSourceKind.PLANNED)) == P2BConflictKind.CROSS_DOMAIN_REUSE_ALLOWED,
            "the same VLAN ID may be reused in another Network Domain");
        Assert(P2BVlanConflictRules.Compare(adjacentLeft, VlanClaim("domain-a", 120, 1500, 2500, P2BSourceKind.OBSERVED)) == P2BConflictKind.VLAN_PLANNED_OBSERVED_COEXISTENCE,
            "planned and observed VLAN facts must coexist for reconciliation");

        var open = new P2BValidityInterval(null, 1000);
        Assert(open.Overlaps(new P2BValidityInterval(999, null)) && !open.Overlaps(new P2BValidityInterval(1000, null)),
            "open-ended intervals must use half-open overlap semantics");
        AssertThrows<ArgumentException>(() => new P2BValidityInterval(2000, 2000).Validate(),
            "VLAN/address validity requires from < to");

        VlanPlan("domain-a", 120, P2BVlanTaggingMode.ACCESS, 120, null, null, null).Validate();
        VlanPlan("domain-a", 120, P2BVlanTaggingMode.TRUNK, null, 120, null, null).Validate();
        VlanPlan("domain-a", 120, P2BVlanTaggingMode.HYBRID, null, 120, null, null).Validate();
        VlanPlan("domain-a", 200, P2BVlanTaggingMode.QINQ, null, null, 200, 300).Validate();
        AssertThrows<ArgumentException>(() => VlanPlan("domain-a", 120, P2BVlanTaggingMode.ACCESS, 121, null, null, null).Validate(),
            "ACCESS PVID must equal VLAN ID");
        AssertThrows<ArgumentException>(() => VlanPlan("domain-a", 120, P2BVlanTaggingMode.TRUNK, null, null, 120, null).Validate(),
            "QinQ fields must not appear on a trunk plan");
        AssertThrows<ArgumentOutOfRangeException>(() => VlanPlan("domain-a", 0, P2BVlanTaggingMode.TRUNK, null, null, null, null).Validate(),
            "VLAN 0 must be rejected");
    }

    private static void VerifiesAuthorityAndEffectiveProjection()
    {
        var planned = new P2BSourceValueFact<string>("same", Source(P2BSourceKind.PLANNED, "plan", 3), "same");
        var observed = new P2BSourceValueFact<string>("same", Source(P2BSourceKind.OBSERVED, "obs", 7), "same");
        var mismatch = observed with { Value = "different", ValueFingerprint = "different" };
        Assert(P2BEffectiveProjectionRules.Evaluate(new[] { planned }).State == P2BEffectiveState.CONSISTENT &&
               P2BEffectiveProjectionRules.Evaluate(new[] { observed }).State == P2BEffectiveState.CONSISTENT,
            "planned-only and observed-only projections must be usable");
        var consistent = P2BEffectiveProjectionRules.Evaluate(new[] { planned, observed });
        Assert(consistent.State == P2BEffectiveState.CONSISTENT && consistent.Value is null && consistent.SupportingSources.Count == 2,
            "matching multi-source facts must be retained without an automatic authority priority");
        Assert(P2BEffectiveProjectionRules.Evaluate(new[] { planned, mismatch }).State == P2BEffectiveState.CONFLICT,
            "mismatched source facts must require review");

        var review = new P2BReviewDecisionContract("address", "device-a", P2BReviewDecision.ACCEPT_OBSERVED,
            new[] { new P2BSourceRevisionReference("plan", 3), new P2BSourceRevisionReference("obs", 7) }, "obs", 7,
            "reviewer", "Observed source verified against field evidence.");
        var selected = P2BEffectiveProjectionRules.Evaluate(new[] { planned, mismatch }, review);
        Assert(selected.State == P2BEffectiveState.MANUALLY_SELECTED && selected.EffectiveSourceReference == "obs" && selected.Value == "different",
            "manual review must select an exact source reference and revision");

        var staleReview = review with { SelectedSourceReference = "obs", SelectedRevision = 8 };
        Assert(P2BEffectiveProjectionRules.Evaluate(new[] { planned, mismatch }, staleReview).State == P2BEffectiveState.CONFLICT,
            "a stale review selection must not silently become effective");
        Assert(selected.SupportingSources.Any(x => x.SourceReference == "obs" && x.Revision == 7),
            "effective projection must preserve source revision provenance");
    }

    private static void VerifiesErrorsAndFeatureGate()
    {
        var response = new P2BErrorResponse(P2BErrorCodes.AddressAlreadyClaimed, "device-address", 12, 11,
            new[] { new P2BConflictDetail("networkDomainId", P2BErrorCodes.AddressAlreadyClaimed, 12, 11) }, false);
        response.Validate();
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert(json.Contains("\"code\":\"ADDRESS_ALREADY_CLAIMED\"", StringComparison.Ordinal) &&
               !json.Contains("deviceId", StringComparison.OrdinalIgnoreCase) && !json.Contains("192.0.2", StringComparison.Ordinal),
            "error responses must be camel-case and redact device/IP details from conflict metadata");
        Assert(P2BErrorCodes.StatusCode(P2BErrorCodes.AddressAlreadyClaimed) == 409 &&
               P2BErrorCodes.StatusCode(P2BErrorCodes.AuthenticationRequired) == 401 &&
               P2BErrorCodes.StatusCode(P2BErrorCodes.Forbidden) == 403 &&
               P2BErrorCodes.StatusCode(P2BErrorCodes.NotFound) == 404 &&
               P2BErrorCodes.StatusCode(P2BErrorCodes.ResourceLimit) == 429 &&
               P2BErrorCodes.StatusCode(P2BErrorCodes.InvalidArgument) == 400,
            "P2B error codes must map to stable HTTP semantics");
        Assert(P2BFeatureGate.FromEnvironment("0") == P2BFeatureState.Disabled &&
               P2BFeatureGate.FromEnvironment("1") == new P2BFeatureState(true, false, false),
            "P2B feature gate must default off and never imply schema/API side effects");
        Assert(!File.Exists(Path.Combine(AppContext.BaseDirectory, "schema_v13.sql")) &&
               !File.Exists(Path.Combine(AppContext.BaseDirectory, "storage", "schema_v13.sql")),
            "P2B-1 must not ship a Schema v13 migration");
    }

    private static void VerifiesReadOnlySchemaV13Preflight()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-p2b1-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(new RuntimeSettings
            {
                DataRoot = root,
                WebPort = 0,
                WebBindAddress = "127.0.0.1",
                WebLanEnabled = false,
                SyslogPort = 0,
                SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024,
                H3cEnabled = false,
                TerminalEnabled = false,
                TerminalDevelopmentSimulationEnabled = false,
                H3cDevelopmentEnvironmentCredentialsEnabled = false
            });
            catalog.Initialize();

            using (var setup = Open(root))
            using (var transaction = setup.BeginTransaction())
            {
                Execute(setup, transaction, "INSERT OR IGNORE INTO sites(id,name) VALUES('site-a','Site A'),('site-b','Site B')");
                const string sql = """
                    INSERT INTO device_inventory
                        (id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,created_ms,updated_ms,terminal_profile_id)
                    VALUES($id,$name,$ip,'TEST','MODEL','NETWORK_DEVICE',$site,NULL,'synthetic v12 fixture',1,1000,1000,NULL)
                    """;
                InsertDevice(setup, transaction, sql, "legacy-a", "192.0.2.10", "site-a");
                InsertDevice(setup, transaction, sql, "legacy-b", "192.0.2.10", "site-b");
                InsertDevice(setup, transaction, sql, "legacy-c", "2001:db8::10", "site-a");
                transaction.Commit();
            }

            using var readOnly = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")};Mode=ReadOnly;Pooling=False;Foreign Keys=True");
            readOnly.Open();
            var report = P2B1SchemaV13Preflight.Analyze(readOnly);
            Assert(report.SchemaV12Health == "PASS" && report.SchemaVersion == 12 &&
                   report.LegacyDeviceCount == 3 && report.LegacyAddressCount == 3 &&
                   report.MissingSiteReferences == 0 && report.DuplicateAddressClaims == 1 &&
                   report.UnresolvedDomainCount == 3 && report.ExpectedV13BackfillCount == 3 &&
                   report.ForeignKeyErrors.Count == 0 && report.MigrationBlockers.Contains("LEGACY_DUPLICATE_ADDRESS_CLAIM_REVIEW"),
                $"synthetic v12 read-only preflight report is unexpected: {report}");
            Assert(report.MigrationBlockers.All(x => x != "SCHEMA_V13_EXECUTED") &&
                   ReadInt(readOnly, "SELECT COUNT(*) FROM schema_versions WHERE version=13") == 0 &&
                   ReadInt(readOnly, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%v13%'") == 0,
                "preflight must not execute or create Schema v13");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static P2BDeviceAddressFact Fact(string deviceId, string? domainId, P2BNormalizedAddress address,
        P2BSourceKind kind, string sourceReference, long revision, long? validFrom, long? validTo, string? fingerprint = null) =>
        new(deviceId, domainId, address, P2BAddressRole.MANAGEMENT, Source(kind, sourceReference, revision),
            new P2BValidityInterval(validFrom, validTo), P2BLifecycleState.ACTIVE, fingerprint);

    private static P2BSourceProvenance Source(P2BSourceKind kind, string reference, long revision = 1) =>
        new(kind, reference, revision, 1000);

    private static P2BVlanClaim VlanClaim(string domainId, int vlanId, long? from, long? to, P2BSourceKind sourceKind) =>
        new(domainId, vlanId, new P2BValidityInterval(from, to), P2BLifecycleState.ACTIVE, sourceKind,
            sourceKind == P2BSourceKind.OBSERVED ? "observed" : "planned", $"{domainId}:{vlanId}:{from}:{to}:{sourceKind}");

    private static P2BVlanPlanContract VlanPlan(string domainId, int vlanId, P2BVlanTaggingMode mode,
        int? pvid, int? nativeVlan, int? outer, int? inner) =>
        new("plan-" + vlanId, domainId, vlanId, mode, nativeVlan, pvid, outer, inner,
            new P2BValidityInterval(null, null), P2BLifecycleState.ACTIVE, Source(P2BSourceKind.PLANNED, "vlan-plan"));

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void InsertDevice(SqliteConnection connection, SqliteTransaction transaction, string sql,
        string id, string ip, string site)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", id);
        command.Parameters.AddWithValue("$ip", ip);
        command.Parameters.AddWithValue("$site", site);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (IOException) when (OperatingSystem.IsWindows()) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Thread.Sleep(50); }
        }
        if (Directory.Exists(path)) throw new IOException($"Could not remove synthetic preflight root: {path}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("P2B-1 contract assertion failed: " + message);
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("P2B-1 contract assertion failed: " + message);
    }
}
