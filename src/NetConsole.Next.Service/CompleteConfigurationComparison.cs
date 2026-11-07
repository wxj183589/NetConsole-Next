using System.Text;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationDiffLine(string Kind, string Text);
internal sealed record CompleteConfigurationContextDiff(string Context, IReadOnlyList<string> Categories,
    int AddedLines, int RemovedLines, bool SensitiveValuesChanged, bool Truncated,
    IReadOnlyList<CompleteConfigurationDiffLine> Lines);
internal sealed record CompleteConfigurationComparison(CompleteConfigurationBackup Before, CompleteConfigurationBackup After,
    bool OriginalConfigurationChanged, bool SensitiveValuesChanged, int AddedLines, int RemovedLines,
    bool Truncated, IReadOnlyList<CompleteConfigurationContextDiff> Contexts);
internal sealed record CompleteConfigurationRestorePreview(string BackupId, string CurrentBackupId, bool Compatible,
    bool WriteAllowed, IReadOnlyList<string> Blockers, CompleteConfigurationComparison Comparison);

internal sealed partial class SimulatorConfigurationService
{
    public CompleteConfigurationComparison Compare(string beforeId, string afterId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        var before = Find(beforeId) ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "比较源备份不可读或完整性校验失败。");
        var after = Find(afterId) ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "比较目标备份不可读或完整性校验失败。");
        var target = Target(before.ProfileId, actor, scope);
        var device = _catalog.GetTaskScopeTarget(target.Id, scope)!;
        Match(before, target, device);
        Match(after, target, device);
        var originalBefore = Encoding.UTF8.GetString(VerifiedBytes(before, false));
        var originalAfter = Encoding.UTF8.GetString(VerifiedBytes(after, false));
        var contexts = CompleteConfigurationDiffer.Compare(originalBefore, originalAfter);
        return new(before, after, Normalize(originalBefore) != Normalize(originalAfter),
            CompleteConfigurationDiffer.SecretsChanged(originalBefore, originalAfter),
            contexts.Sum(item => item.AddedLines), contexts.Sum(item => item.RemovedLines),
            contexts.Any(item => item.Truncated), contexts);
    }

    public async Task<CompleteConfigurationRestorePreview> PreviewRestoreAsync(string backupId, WebUserIdentity actor,
        P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        // Validate the source before opening a device connection. CaptureAsync performs
        // live scope checks and persists a verified full current snapshot, without writing.
        var backup = View(backupId, actor, scope)?.Backup
            ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "恢复源备份不可读或完整性校验失败。");
        var current = await CaptureAsync(backup.ProfileId, actor, scope, cancellationToken).ConfigureAwait(false);
        var comparison = Compare(current.Id, backup.Id, actor, scope);
        var compatible = backup.Model == current.Model && backup.SoftwareVersion == current.SoftwareVersion;
        var blockers = new List<string> { "FULL_DEVICE_RESTORE_NOT_IMPLEMENTED" };
        if (!compatible) blockers.Add("MODEL_OR_SOFTWARE_VERSION_MISMATCH");
        Audit(actor, "CONFIG_COMPLETE_RESTORE_PREVIEW", backup.Id, backup.ProfileId, "READ_ONLY");
        return new(backup.Id, current.Id, compatible, false, blockers, comparison);
    }
}

internal static class CompleteConfigurationDiffer
{
    private const int MaximumCells = 1_048_576;
    private const int MaximumPreviewLines = 512;
    private static readonly Regex PrivateKey = new(@"-----BEGIN [^\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^\r\n]*PRIVATE KEY-----|\z)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private sealed record Block(string Key, string Header, string Text);

    internal static bool SecretsChanged(string before, string after) => Secrets(before) != Secrets(after);
    private static string Secrets(string text)
    {
        var keys = PrivateKey.Matches(text).Select(match => match.Value).ToArray();
        var remainder = PrivateKey.Replace(text, "");
        var lines = remainder.Split('\n').Where(line => Public(line) != line);
        return string.Join('\n', keys.Concat(lines));
    }
    private static string Public(string text) => ConfigBackupSanitizer.Sanitize("display current-configuration", text);

    internal static IReadOnlyList<CompleteConfigurationContextDiff> Compare(string before, string after)
    {
        var originalBefore = SimulatorConfigurationService.Normalize(before);
        var originalAfter = SimulatorConfigurationService.Normalize(after);
        // Redact entire multiline keys before stanza splitting: a separator-like
        // line inside a key must never leave an unredacted fragment in a new block.
        var left = Blocks(PrivateKey.Replace(originalBefore, "<REDACTED>"));
        var right = Blocks(PrivateKey.Replace(originalAfter, "<REDACTED>"));
        var rightMap = right.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var leftKeys = left.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var result = new List<CompleteConfigurationContextDiff>();
        var cellsRemaining = MaximumCells;
        var previewRemaining = 2048;
        if (!PrivateKey.Matches(originalBefore).Select(match => match.Value)
            .SequenceEqual(PrivateKey.Matches(originalAfter).Select(match => match.Value)))
            result.Add(new("私钥配置（内容已隐藏）", ["GENERAL"], 0, 0, true, false, []));
        foreach (var first in left)
        {
            rightMap.TryGetValue(first.Key, out var second);
            Add(first, second);
        }
        foreach (var second in right.Where(item => !leftKeys.Contains(item.Key))) Add(null, second);
        var commonBefore = left.Where(item => rightMap.ContainsKey(item.Key)).ToArray();
        var commonAfter = right.Where(item => leftKeys.Contains(item.Key)).ToArray();
        if (!commonBefore.Select(item => item.Key).SequenceEqual(commonAfter.Select(item => item.Key)))
        {
            var (lines, added, removed, truncated) = Diff(commonBefore.Select(item => Public(item.Header)).ToArray(),
                commonAfter.Select(item => Public(item.Header)).ToArray(), ref cellsRemaining, ref previewRemaining);
            result.Add(new("配置段顺序", ["GENERAL"], added, removed, false, truncated, lines));
        }
        return result;

        void Add(Block? first, Block? second)
        {
            if (first?.Text == second?.Text) return;
            var oldText = first?.Text ?? "";
            var newText = second?.Text ?? "";
            var oldLines = first is null ? [] : Public(oldText).Split('\n');
            var newLines = second is null ? [] : Public(newText).Split('\n');
            var (lines, added, removed, truncated) = Diff(oldLines, newLines, ref cellsRemaining, ref previewRemaining);
            var header = Public(second?.Header ?? first!.Header).Trim();
            result.Add(new(header, Categories(oldText + "\n" + newText), added, removed,
                SecretsChanged(oldText, newText), truncated, lines));
        }
    }

    private static IReadOnlyList<Block> Blocks(string text)
    {
        var blocks = new List<Block>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim() == "#") { Flush(); continue; }
            if (!string.IsNullOrWhiteSpace(line)) current.Add(line.TrimEnd());
        }
        Flush();
        return blocks;
        void Flush()
        {
            if (current.Count == 0) return;
            var header = current[0].Trim();
            var occurrence = occurrences.GetValueOrDefault(header) + 1;
            occurrences[header] = occurrence;
            blocks.Add(new(header + "\0" + occurrence, header, string.Join('\n', current)));
            current.Clear();
        }
    }

    private static string[] Categories(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).ToArray();
        var categories = new List<string>();
        if (lines.Any(line => line.StartsWith("interface ", StringComparison.Ordinal))) categories.Add("INTERFACE");
        if (lines.Any(line => Regex.IsMatch(line, @"^(vlan |port (access|trunk|hybrid) vlan )"))) categories.Add("VLAN");
        if (lines.Any(line => Regex.IsMatch(line, @"^(wlan |wireless |ssid |service-template |radio )"))) categories.Add("WIRELESS");
        if (lines.Any(line => Regex.IsMatch(line, @"^(wlan )?ap-group "))) categories.Add("AP_GROUP");
        if (lines.Any(line => Regex.IsMatch(line, @"^(ip(v6)? address |management |mad ip address )"))) categories.Add("MANAGEMENT_ADDRESS");
        if (lines.Any(line => Regex.IsMatch(line, @"^(acl |packet-filter |traffic-filter )"))) categories.Add("ACL");
        if (lines.Any(line => Regex.IsMatch(line, @"^(ip(v6)? route-static |ospf|bgp |rip |isis |route-policy )"))) categories.Add("ROUTING");
        if (categories.Count == 0) categories.Add("GENERAL");
        return categories.ToArray();
    }

    private static (IReadOnlyList<CompleteConfigurationDiffLine> Lines, int Added, int Removed, bool Truncated) Diff(string[] before, string[] after,
        ref int cellsRemaining, ref int previewRemaining)
    {
        var lines = new List<CompleteConfigurationDiffLine>();
        var added = 0; var removed = 0; var total = 0;
        var previewLimit = Math.Min(MaximumPreviewLines, previewRemaining);
        void Emit(string kind, string text)
        {
            if (kind == "ADDED") added++;
            if (kind == "REMOVED") removed++;
            total++;
            if (lines.Count < previewLimit) lines.Add(new(kind, text));
        }
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var oldEnd = before.Length; var newEnd = after.Length;
        while (oldEnd > prefix && newEnd > prefix && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        // Include nearby unchanged lines, preserving ordering and duplicate commands.
        foreach (var line in before.Skip(Math.Max(0, prefix - 3)).Take(Math.Min(3, prefix))) Emit("CONTEXT", line);
        var n = oldEnd - prefix; var m = newEnd - prefix;
        var cells = (long)(n + 1) * (m + 1);
        var bounded = cells <= cellsRemaining;
        if (bounded)
        {
            cellsRemaining -= (int)cells;
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    table[i, j] = before[prefix + i] == after[prefix + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            var x = 0; var y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && before[prefix + x] == after[prefix + y]) { Emit("CONTEXT", before[prefix + x++]); y++; }
                else if (x < n && (y == m || table[x + 1, y] >= table[x, y + 1])) Emit("REMOVED", before[prefix + x++]);
                else Emit("ADDED", after[prefix + y++]);
            }
        }
        else
        {
            foreach (var line in before.Skip(prefix).Take(n)) Emit("REMOVED", line);
            foreach (var line in after.Skip(prefix).Take(m)) Emit("ADDED", line);
        }
        foreach (var line in before.Skip(oldEnd).Take(3)) Emit("CONTEXT", line);
        previewRemaining -= lines.Count;
        return (lines, added, removed, !bounded || total > previewLimit);
    }
}
