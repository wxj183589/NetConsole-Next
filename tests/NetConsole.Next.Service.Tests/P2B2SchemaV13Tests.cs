using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class P2B2SchemaV13Tests
{
    public static void Run()
    {
        var previousGate = Environment.GetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable);
        Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, "1");
        try
        {
            VerifiesIsolatedMigrationBackfillAndRestartCompatibility();
            VerifiesV13RelationsAndIntegrity();
            VerifiesTransactionalFaultRecovery();
            VerifiesMigrationAuthorizationBoundary();
            P2B2MigrationPathSecurityTests.Run();
        }
        finally
        {
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, previousGate);
        }
        Console.WriteLine("PASS: P2B-2 Schema v13 isolated migration, legacy backfill, relations, integrity and crash recovery");
    }

    private static void VerifiesIsolatedMigrationBackfillAndRestartCompatibility()
    {
        var root = CreateV12Fixture("netconsole-next-p2b2-migrate-");
        try
        {
            var databasePath = Path.Combine(root, "catalog.db");
            using (var before = Open(root))
            {
                var report = P2B1SchemaV13Preflight.Analyze(before);
                Assert(report.SchemaVersion == 12 && report.ExpectedV13BackfillCount == 3 &&
                       report.DuplicateAddressClaims == 1 && report.ForeignKeyErrors.Count == 0,
                    "P2B-2 must start from the read-only v12 preflight contract");
                Assert(ReadInt(before, "SELECT COUNT(*) FROM sqlite_master WHERE name='network_domains'") == 0,
                    "the v12 preflight must not create v13 tables");
            }

            using (var connection = Open(root))
            {
                var result = P2B2SchemaV13Migration.Apply(connection,
                    P2B2MigrationContext.ForIsolatedTest(databasePath));
                Assert(!result.AlreadyApplied && result.SchemaVersion == 13 && result.LegacyBackfilled == 3 &&
                       result.PendingDomainCount == 1 && result.ConflictCount == 2,
                    $"isolated v12 to v13 migration result is unexpected: {result}");

                var repository = new P2B2SchemaRepository(connection);
                var snapshot = repository.ReadSnapshot();
                Assert(snapshot.SchemaVersion == 13 && snapshot.DeviceAddressCount == 3 &&
                       snapshot.DeviceAddressClaimCount == 3 && snapshot.LegacyResolutionCount == 3 &&
                       snapshot.PendingDomainCount == 1 && snapshot.ConflictCount == 2 &&
                       snapshot.NetworkDomainCount == 0 && snapshot.VlanPlanCount == 0,
                    $"v13 backfill snapshot is unexpected: {snapshot}");
                Assert(repository.ReadDeviceAddresses().Count == 3 &&
                       repository.ReadLegacyResolutions().Count == 3 &&
                       repository.ReadLegacyResolutions().Single(x => x.DeviceId == "v13-device-a").TerminalProfileId == "v13-profile",
                    "legacy address facts and terminal-profile provenance must be queryable through the internal repository");

                var second = P2B2SchemaV13Migration.Apply(connection,
                    P2B2MigrationContext.ForIsolatedTest(databasePath));
                Assert(second.AlreadyApplied && second.LegacyBackfilled == 3 && second.PendingDomainCount == 1 &&
                       second.ConflictCount == 2 && ReadInt(connection, "SELECT COUNT(*) FROM schema_versions WHERE version=13") == 1,
                    "re-running v13 migration must be idempotent without duplicate facts or version rows");
            }

            var restarted = new Catalog(TestSettings(root));
            restarted.Initialize();
            Assert(restarted.QueryManagedDeviceInventory(pageSize: 20).Total == 3,
                "Catalog restart must continue reading the original inventory after candidate v13 migration");
            Assert(restarted.GetDeviceInventoryDetail("v13-device-a")!.Item.ManagementIp == "192.0.2.10" &&
                   restarted.GetH3cDeviceProfile("v13-profile")!.Host == "192.0.2.10",
                "legacy management_ip and terminal profile host must remain unchanged by the additive migration");
            using var afterRestart = Open(root);
            var afterSnapshot = new P2B2SchemaRepository(afterRestart).ReadSnapshot();
            Assert(afterSnapshot.SchemaVersion == 13 && afterSnapshot.DeviceAddressCount == 3 &&
                   afterSnapshot.DeviceOperationCount == 0,
                "restart must preserve the v13 store and the pre-existing v12 operation boundary");
        }
        finally { DeleteDirectory(root); }
    }

    private static void VerifiesV13RelationsAndIntegrity()
    {
        var root = CreateV12Fixture("netconsole-next-p2b2-relations-");
        try
        {
            var databasePath = Path.Combine(root, "catalog.db");
            using var connection = Open(root);
            P2B2SchemaV13Migration.Apply(connection, P2B2MigrationContext.ForIsolatedTest(databasePath));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                INSERT INTO network_domains
                    (id,name,domain_kind,parent_domain_id,vrf_name,source_kind,source_reference,lifecycle,scope_resolution,revision,created_ms,updated_ms)
                VALUES
                    ('domain-a','P2B2 management','MANAGEMENT',NULL,'mgmt','PLANNED','p2b2-domain-a','ACTIVE','RESOLVED',1,$now,$now),
                    ('domain-b','P2B2 shared','SHARED_SERVICE','domain-a','shared','OBSERVED','p2b2-domain-b','ACTIVE','UNRESOLVED',1,$now,$now)
                """, ("$now", now));
            Execute(connection, transaction, """
                INSERT INTO network_domain_scopes
                    (id,network_domain_id,scope_kind,scope_reference,resolution,source_kind,source_reference,revision,created_ms,updated_ms)
                VALUES('scope-a','domain-a','SITE','local','RESOLVED','PLANNED','p2b2-scope-a',1,$now,$now)
                """, ("$now", now));
            Execute(connection, transaction, """
                INSERT INTO device_domain_memberships
                    (id,device_id,network_domain_id,membership_role,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
                VALUES('membership-a','v13-device-a','domain-a','PRIMARY','PLANNED','p2b2-membership-a','fp-membership-a',1,'ACTIVE',$now,$now)
                """, ("$now", now));
            InsertAddress(connection, transaction, "source-address-a", "v13-device-a", "domain-a", "192.0.2.20",
                "PLANNED", "plan-a", "fp-a", "RESOLVED", "NONE", now);
            InsertAddress(connection, transaction, "source-address-b", "v13-device-b", "domain-a", "192.0.2.20",
                "OBSERVED", "obs-b", "fp-b", "RESOLVED", "NONE", now);
            InsertClaim(connection, transaction, "source-claim-a", "source-address-a", "v13-device-a", "domain-a",
                "192.0.2.20", "ACTIVE", "NONE", now);
            InsertClaim(connection, transaction, "source-claim-b", "source-address-b", "v13-device-b", "domain-a",
                "192.0.2.20", "ACTIVE", "NONE", now);
            Execute(connection, transaction, """
                INSERT INTO vlan_plans
                    (id,network_domain_id,vlan_id,name,purpose,tagging_mode,native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,
                     scope_resolution,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
                VALUES('vlan-plan-a','domain-a',120,'Management','planned access','ACCESS',120,120,NULL,NULL,
                       'RESOLVED','PLANNED','p2b2-vlan-plan','fp-vlan-plan',1,'ACTIVE',$now,$now)
                """, ("$now", now));
            Execute(connection, transaction, """
                INSERT INTO vlan_assignments
                    (id,device_id,network_domain_id,vlan_plan_id,interface_reference,vlan_id,assignment_role,tagging_mode,
                     native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
                VALUES('vlan-assignment-a','v13-device-a','domain-a','vlan-plan-a','GigabitEthernet1/0/1',120,'ACCESS','ACCESS',120,120,NULL,NULL,
                       'OBSERVED','p2b2-vlan-observation','fp-vlan-observation',1,'ACTIVE',$now,$now)
                """, ("$now", now));
            transaction.Commit();

            var repository = new P2B2SchemaRepository(connection);
            var snapshot = repository.ReadSnapshot();
            Assert(snapshot.NetworkDomainCount == 2 && snapshot.NetworkDomainScopeCount == 1 &&
                   snapshot.DeviceDomainMembershipCount == 1 && snapshot.DeviceAddressCount == 5 &&
                   snapshot.DeviceAddressClaimCount == 5 && snapshot.VlanPlanCount == 1 &&
                   snapshot.VlanAssignmentCount == 1,
                $"v13 relation counts are unexpected: {snapshot}");
            Assert(repository.ReadNetworkDomains().Single(x => x.Id == "domain-b").ScopeResolution == "UNRESOLVED" &&
                   repository.ReadDeviceAddresses("v13-device-a").Count == 2,
                "domain resolution and device-scoped address reads must preserve provenance rows");

            var integrity = ReadScalar(connection, "PRAGMA integrity_check")?.ToString();
            var foreignKeyErrors = ReadInt(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check");
            var journalMode = ReadScalar(connection, "PRAGMA journal_mode")?.ToString();
            Assert(string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) && foreignKeyErrors == 0 &&
                   string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase),
                $"candidate v13 storage must pass SQLite integrity, foreign-key and WAL checks: integrity={integrity}, foreignKeyErrors={foreignKeyErrors}, journalMode={journalMode}");
            AssertThrows<SqliteException>(() => Execute(connection, null,
                "INSERT INTO vlan_plans(id,network_domain_id,vlan_id,name,purpose,tagging_mode,scope_resolution,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms) " +
                "VALUES('bad-vlan','domain-a',0,'bad','','TRUNK','RESOLVED','MANUAL','bad','bad',1,'ACTIVE',1,1)"),
                "VLAN IDs outside 1..4094 must be rejected by schema constraints");
        }
        finally { DeleteDirectory(root); }
    }

    private static void VerifiesTransactionalFaultRecovery()
    {
        foreach (var point in new[]
        {
            P2B2MigrationFaultPoint.BEFORE_TRANSACTION,
            P2B2MigrationFaultPoint.AFTER_SCHEMA,
            P2B2MigrationFaultPoint.AFTER_FIRST_BACKFILL,
            P2B2MigrationFaultPoint.AFTER_BACKFILL_BEFORE_VERSION,
            P2B2MigrationFaultPoint.AFTER_VERSION_BEFORE_COMMIT
        })
        {
            var root = CreateV12Fixture("netconsole-next-p2b2-fault-");
            try
            {
                using (var connection = Open(root))
                {
                    AssertThrows<InvalidOperationException>(() => P2B2SchemaV13Migration.Apply(connection,
                        P2B2MigrationContext.ForIsolatedTest(Path.Combine(root, "catalog.db"), point)),
                        $"fault point {point} must abort the isolated transaction");
                }
                using var after = Open(root);
                Assert(ReadInt(after, "SELECT COALESCE(MAX(CASE WHEN version <= 12 THEN version END),0) FROM schema_versions") == 12 &&
                       ReadInt(after, "SELECT COUNT(*) FROM sqlite_master WHERE name='network_domains'") == 0,
                    $"fault point {point} must leave the database at v12 without partial v13 tables");
            }
            finally { DeleteDirectory(root); }
        }

        var committedRoot = CreateV12Fixture("netconsole-next-p2b2-crash-");
        try
        {
            var exitCode = RunMigrationChild(committedRoot, P2B2MigrationFaultPoint.AFTER_COMMIT, crash: true);
            Assert(exitCode != 0, "post-commit crash harness must terminate the child process abnormally");
            using (var connection = Open(committedRoot))
            {
                var snapshot = new P2B2SchemaRepository(connection).ReadSnapshot();
                Assert(snapshot.SchemaVersion == 13 && snapshot.DeviceAddressCount == 3,
                    "a crash after commit must leave a complete v13 transaction, never a partial commit");
            }
            var restartExit = RunMigrationChild(committedRoot, P2B2MigrationFaultPoint.NONE, crash: false);
            Assert(restartExit == 0, "a post-commit restart must treat the migration as already applied");
        }
        finally { DeleteDirectory(committedRoot); }
    }

    private static void VerifiesMigrationAuthorizationBoundary()
    {
        var root = CreateV12Fixture("netconsole-next-p2b2-auth-");
        try
        {
            using var connection = Open(root);
            var previous = Environment.GetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable);
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, null);
            try
            {
                AssertThrows<InvalidOperationException>(() => P2B2SchemaV13Migration.Apply(connection,
                    P2B2MigrationContext.ForIsolatedTest(Path.Combine(root, "catalog.db"))),
                    "feature gating or normal Catalog startup must not authorize v13 migration");
            }
            finally { Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, previous); }
            Assert(ReadInt(connection, "SELECT COALESCE(MAX(CASE WHEN version <= 12 THEN version END),0) FROM schema_versions") == 12,
                "an unauthorized migration attempt must not mutate v12");
        }
        finally { DeleteDirectory(root); }
    }

    private static string CreateV12Fixture(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            new Catalog(TestSettings(root)).Initialize();
            using var connection = Open(root);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "INSERT OR IGNORE INTO sites(id,name) VALUES('site-b','P2B2 Site B')");
            Execute(connection, transaction, """
                INSERT INTO collection_sources(id,site_id,kind,label) VALUES('v13-source','local','H3C_SSH','P2B2 profile source')
                """);
            Execute(connection, transaction, """
                INSERT INTO h3c_device_profiles
                    (id,source_id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms,
                     host_key_update_policy,allow_legacy_algorithms,terminal_encoding,terminal_protocol,allow_telnet_fallback)
                VALUES('v13-profile','v13-source','192.0.2.10',22,'reader','P2B2_CREDENTIAL','SHA256:p2b2',1,10,10,
                       'AUTO_UPDATE',0,'AUTO','SSH',0)
                """);
            InsertDevice(connection, transaction, "v13-device-a", "Device A", "192.0.2.10", "local", "v13-profile");
            InsertDevice(connection, transaction, "v13-device-b", "Device B", "192.0.2.10", "site-b", null);
            InsertDevice(connection, transaction, "v13-device-c", "Device C", "2001:db8::10", "local", null);
            transaction.Commit();
            return root;
        }
        catch
        {
            DeleteDirectory(root);
            throw;
        }
    }

    private static void InsertDevice(SqliteConnection connection, SqliteTransaction transaction, string id,
        string name, string managementIp, string siteId, string? profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_inventory
                (id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,created_ms,updated_ms,terminal_profile_id)
            VALUES($id,$name,$ip,'TEST','MODEL','NETWORK_DEVICE',$site,NULL,'P2B2 fixture',1,100,100,$profile)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$ip", managementIp);
        command.Parameters.AddWithValue("$site", siteId);
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertAddress(SqliteConnection connection, SqliteTransaction transaction, string id, string deviceId,
        string domainId, string address, string sourceKind, string sourceReference, string fingerprint,
        string domainState, string conflictState, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_addresses
                (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,address_role,source_kind,source_reference,
                 source_fingerprint,revision,domain_resolution_state,conflict_state,lifecycle,created_ms,updated_ms)
            VALUES($id,$device,$domain,'IPV4',$address,32,'MANAGEMENT',$sourceKind,$sourceReference,$fingerprint,1,$domainState,$conflictState,'ACTIVE',$now,$now)
            """;
        Add(command, ("$id", id), ("$device", deviceId), ("$domain", domainId), ("$address", address),
            ("$sourceKind", sourceKind), ("$sourceReference", sourceReference), ("$fingerprint", fingerprint),
            ("$domainState", domainState), ("$conflictState", conflictState), ("$now", now));
        command.ExecuteNonQuery();
    }

    private static void InsertClaim(SqliteConnection connection, SqliteTransaction transaction, string id, string addressId,
        string deviceId, string domainId, string address, string state, string conflictCode, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_address_claims
                (id,device_address_id,device_id,network_domain_id,address_family,normalized_address,lifecycle,claim_state,conflict_code,created_ms,updated_ms)
            VALUES($id,$addressId,$device,$domain,'IPV4',$address,'ACTIVE',$state,$conflict,$now,$now)
            """;
        Add(command, ("$id", id), ("$addressId", addressId), ("$device", deviceId), ("$domain", domainId),
            ("$address", address), ("$state", state), ("$conflict", conflictCode), ("$now", now));
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
    }

    private static int RunMigrationChild(string root, P2B2MigrationFaultPoint point, bool crash)
    {
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Test entry assembly is unavailable.");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var processName = Path.GetFileNameWithoutExtension(processPath);
        if (Path.GetExtension(processPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryAssembly);
        start.ArgumentList.Add("--p2b2-migrate");
        start.ArgumentList.Add(Path.Combine(root, "catalog.db"));
        start.ArgumentList.Add(point.ToString());
        start.ArgumentList.Add(crash ? "crash" : "throw");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the P2B-2 child process.");
        child.WaitForExit();
        var output = child.StandardOutput.ReadToEnd();
        var error = child.StandardError.ReadToEnd();
        Console.WriteLine($"P2B2 child {point}/{(crash ? "crash" : "throw")}: exit={child.ExitCode}; stdout={output.Trim()}; stderr={error.Trim()}");
        return child.ExitCode;
    }

    private static RuntimeSettings TestSettings(string root) => new()
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

    private static object? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long ReadInt(SqliteConnection connection, string sql) =>
        Convert.ToInt64(ReadScalar(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("P2B-2 schema assertion failed: " + message);
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("P2B-2 schema assertion failed: " + message);
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 40 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) when (OperatingSystem.IsWindows()) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Thread.Sleep(100); }
        }
        if (Directory.Exists(path)) throw new IOException($"Could not remove P2B-2 fixture root: {path}");
    }
}

internal static class P2B2MigrationChild
{
    public static Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 3)
            throw new ArgumentException("Expected: --p2b2-migrate <database-path> <fault-point> <crash|throw>.");
        var databasePath = Path.GetFullPath(arguments[0]);
        var point = Enum.Parse<P2B2MigrationFaultPoint>(arguments[1], ignoreCase: false);
        var crash = string.Equals(arguments[2], "crash", StringComparison.Ordinal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        var result = P2B2SchemaV13Migration.Apply(connection,
            P2B2MigrationContext.ForIsolatedTest(databasePath, point, crash));
        Console.WriteLine($"P2B2 child migration complete: {result}");
        return Task.CompletedTask;
    }
}
