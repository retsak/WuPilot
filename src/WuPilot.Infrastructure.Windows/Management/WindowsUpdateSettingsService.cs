using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Infrastructure.Windows.Management;

public sealed class WindowsUpdateSettingsService(string? auditPath = null) : IWindowsUpdateSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _auditPath = auditPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "settings-audit.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(ReadSnapshot, cancellationToken);

    public async Task<SettingChangeResult> ApplyAsync(IEnumerable<SettingChange> changes, CancellationToken cancellationToken)
    {
        var requested = changes.ToArray();
        if (requested.Length == 0) throw new ArgumentException("At least one setting change is required.", nameof(changes));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requested.Select(static change => change.PolicyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
                throw new ArgumentException("A settings batch cannot contain the same policy more than once.", nameof(changes));
            var definitions = requested.Select(change =>
                PolicyCatalog.All.FirstOrDefault(item => item.Id == change.PolicyId) ??
                throw new ArgumentException($"Unknown policy: {change.PolicyId}.")).ToArray();
            for (var index = 0; index < requested.Length; index++) Validate(definitions[index], requested[index]);

            var batchId = Guid.NewGuid();
            var build = ReadBuild().ToString(CultureInfo.InvariantCulture);
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
            var issues = new List<SettingChangeIssue>();
            for (var index = 0; index < requested.Length; index++)
            {
                if (!CanWrite(definitions[index]))
                    issues.Add(new(definitions[index].Id, "view-only", $"{definitions[index].DisplayName} is view-only on this Windows build."));
                var current = Format(ReadRaw(definitions[index]).Value);
                if (requested[index].EnforceExpectedRequestedValue &&
                    !string.Equals(current, requested[index].ExpectedRequestedValue, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(definitions[index].Id, "drift", $"{definitions[index].DisplayName} changed after it was staged."));
            }
            if (issues.Count > 0)
            {
                var summary = string.Join(" ", issues.Select(static issue => issue.Message));
                var entry = new SettingAuditEntry(Guid.NewGuid(), batchId, DateTimeOffset.Now, "batch", "Settings batch", null, null, null,
                    PolicyOwnership.Local, build, sid, false, false, $"Validation stopped: {summary}");
                await AppendAuditAsync([entry], cancellationToken).ConfigureAwait(false);
                return new(batchId, false, summary, ReadSnapshot().Policies, [entry], issues);
            }
            var originals = new List<(PolicyDefinition Definition, object? Value, RegistryValueKind Kind, bool Existed)>();
            var audit = new List<SettingAuditEntry>();
            try
            {
                for (var index = 0; index < requested.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var definition = definitions[index];
                    var original = ReadRaw(definition);
                    originals.Add((definition, original.Value, original.Kind, original.Existed));
                    Write(definition, requested[index]);
                    var verified = ReadRaw(definition);
                    var expected = requested[index].Remove ? null : Normalize(definition, requested[index].Value);
                    var actual = Format(verified.Value);
                    if (requested[index].Remove ? verified.Existed : !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Windows did not retain the requested value for {definition.DisplayName}.");
                    }
                    audit.Add(new(Guid.NewGuid(), batchId, DateTimeOffset.Now, definition.Id, definition.DisplayName,
                        Format(original.Value), actual, actual, Ownership(definition, original.Existed), build, sid, true, false, "Applied and verified."));
                }
            }
            catch (Exception exception)
            {
                foreach (var original in originals.AsEnumerable().Reverse())
                {
                    RestoreRaw(original.Definition, original.Value, original.Kind, original.Existed);
                }
                audit.Add(new(Guid.NewGuid(), batchId, DateTimeOffset.Now, "batch", "Settings batch", null, null, null,
                    PolicyOwnership.Local, build, sid, false, false, $"Rolled back: {exception.Message}"));
                await AppendAuditAsync(audit, cancellationToken).ConfigureAwait(false);
                return new(batchId, false, exception.Message, ReadSnapshot().Policies, audit);
            }

            await AppendAuditAsync(audit, cancellationToken).ConfigureAwait(false);
            return new(batchId, true, $"{audit.Count} setting change(s) applied and verified.", ReadSnapshot().Policies, audit);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingChangeResult> RestoreAsync(Guid auditEntryId, bool allowConflict, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAuditCoreAsync(cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(item => item.Id == auditEntryId) ??
                throw new InvalidOperationException("The audit entry was not found.");
            var definition = PolicyCatalog.All.First(item => item.Id == entry.PolicyId);
            var current = ReadRaw(definition);
            if (!allowConflict && !string.Equals(Format(current.Value), entry.AfterValue, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The setting changed after this audit entry. Confirm conflict restoration to continue.");
            var change = new SettingChange(definition.Id, entry.BeforeValue, entry.BeforeValue is null);
            Validate(definition, change);
            Write(definition, change);
            var restored = entry with { Restored = true, Message = "Restored by a later WuPilot action." };
            entries[entries.IndexOf(entry)] = restored;
            await WriteAuditCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            var restoreEntry = new SettingAuditEntry(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, definition.Id,
                definition.DisplayName, Format(current.Value), entry.BeforeValue, Format(ReadRaw(definition).Value),
                entry.Ownership, ReadBuild().ToString(CultureInfo.InvariantCulture),
                WindowsIdentity.GetCurrent().User?.Value ?? "unknown", true, false, $"Restored audit entry {entry.Id}.");
            entries.Add(restoreEntry);
            await WriteAuditCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return new(restoreEntry.BatchId, true, "Previous value restored.", ReadSnapshot().Policies, [restoreEntry]);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAuditCoreAsync(cancellationToken).ConfigureAwait(false)).OrderByDescending(static item => item.ChangedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<string> ExportAuditAsync(CancellationToken cancellationToken)
    {
        var entries = await GetAuditAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "Exports");
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(directory, $"settings-audit-{stamp}.json");
        var csvPath = Path.Combine(directory, $"settings-audit-{stamp}.csv");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var csv = new StringBuilder("ChangedAt,PolicyId,DisplayName,Before,After,Verified,Ownership,Succeeded,Restored,Message\r\n");
        foreach (var item in entries)
            csv.AppendLine(string.Join(',', new[] { item.ChangedAt.ToString("O"), item.PolicyId, item.DisplayName, item.BeforeValue, item.AfterValue,
                item.VerifiedValue, item.Ownership.ToString(), item.Succeeded.ToString(), item.Restored.ToString(), item.Message }.Select(Csv)));
        await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return jsonPath;
    }

    private static SettingsSnapshot ReadSnapshot()
    {
        var build = ReadBuild();
        var policies = PolicyCatalog.All.Select(definition =>
        {
            if (definition.IsMdmOnly)
                return new PolicyState(definition, null, ReadMdmSummary(definition.Id), PolicyOwnership.Mdm, true, false, "MDM/CSP-owned; view and export only.");
            var raw = ReadRaw(definition);
            var mdmValue = ReadMdmValue(definition);
            var supported = build >= definition.MinimumBuild;
            var canEdit = supported && CanWrite(definition);
            var owner = mdmValue is null ? Ownership(definition, raw.Existed) : PolicyOwnership.Mdm;
            var status = !supported ? $"Requires Windows build {definition.MinimumBuild} or newer."
                : definition.IsLegacy ? "Legacy policy; evidence only."
                : mdmValue is not null ? "MDM supplies the effective value. A local request may be ignored or reverted."
                : definition.IsPrivateUx ? "Private Windows Settings state; build-gated and readback-verified."
                : raw.Existed ? $"{owner} value configured." : "Not configured; Windows default applies.";
            return new PolicyState(definition, Format(raw.Value), mdmValue ?? Format(raw.Value), owner, supported, canEdit, status);
        }).ToArray();
        return new(DateTimeOffset.Now, build, policies);
    }

    private static bool CanWrite(PolicyDefinition definition) =>
        !definition.IsMdmOnly && !definition.IsLegacy && definition.RegistryPath is not null &&
        (!definition.IsPrivateUx || ReadBuild() >= definition.MinimumBuild);

    private static PolicyOwnership Ownership(PolicyDefinition definition, bool exists) =>
        !exists ? PolicyOwnership.Unconfigured :
        definition.IsPrivateUx ? PolicyOwnership.WindowsUx :
        definition.RegistryPath?.StartsWith(@"SOFTWARE\Policies", StringComparison.OrdinalIgnoreCase) == true ? PolicyOwnership.GroupPolicy :
        PolicyOwnership.Local;

    private static (object? Value, RegistryValueKind Kind, bool Existed) ReadRaw(PolicyDefinition definition)
    {
        if (definition.RegistryPath is null || definition.RegistryValueName is null) return (null, RegistryValueKind.None, false);
        using var key = Registry.LocalMachine.OpenSubKey(definition.RegistryPath);
        var names = key?.GetValueNames() ?? [];
        var existed = names.Contains(definition.RegistryValueName, StringComparer.OrdinalIgnoreCase);
        return existed
            ? (key!.GetValue(definition.RegistryValueName), key.GetValueKind(definition.RegistryValueName), true)
            : (null, RegistryValueKind.None, false);
    }

    private static void Write(PolicyDefinition definition, SettingChange change)
    {
        using var key = Registry.LocalMachine.CreateSubKey(definition.RegistryPath!, writable: true) ??
            throw new UnauthorizedAccessException($"Cannot open HKLM\\{definition.RegistryPath} for writing.");
        if (change.Remove) { key.DeleteValue(definition.RegistryValueName!, throwOnMissingValue: false); return; }
        var normalized = Normalize(definition, change.Value);
        if (definition.ValueKind is PolicyValueKind.Boolean or PolicyValueKind.Integer or PolicyValueKind.Choice)
            key.SetValue(definition.RegistryValueName!, int.Parse(normalized!, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else key.SetValue(definition.RegistryValueName!, normalized ?? string.Empty, RegistryValueKind.String);
    }

    private static void RestoreRaw(PolicyDefinition definition, object? value, RegistryValueKind kind, bool existed)
    {
        using var key = Registry.LocalMachine.CreateSubKey(definition.RegistryPath!, writable: true);
        if (!existed) key?.DeleteValue(definition.RegistryValueName!, false);
        else key?.SetValue(definition.RegistryValueName!, value!, kind);
    }

    private static void Validate(PolicyDefinition definition, SettingChange change)
    {
        if (change.Remove) return;
        var normalized = Normalize(definition, change.Value);
        if (definition.ValueKind is PolicyValueKind.Boolean or PolicyValueKind.Integer or PolicyValueKind.Choice)
        {
            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"{definition.DisplayName} requires an integer value.");
            if (definition.Minimum is not null && number < definition.Minimum || definition.Maximum is not null && number > definition.Maximum)
                throw new ArgumentOutOfRangeException(definition.Id, $"{definition.DisplayName} must be between {definition.Minimum} and {definition.Maximum}.");
            if (definition.Choices is not null && !definition.Choices.ContainsKey(normalized!))
                throw new ArgumentException($"{normalized} is not valid for {definition.DisplayName}.");
        }
    }

    private static string? Normalize(PolicyDefinition definition, string? value) =>
        definition.ValueKind == PolicyValueKind.Boolean ? (value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ? "1" :
            value?.Equals("false", StringComparison.OrdinalIgnoreCase) == true ? "0" : value?.Trim()) : value?.Trim();
    private static string? Format(object? value) => value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static int ReadBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return int.TryParse(Convert.ToString(key?.GetValue("CurrentBuildNumber"), CultureInfo.InvariantCulture), out var build) ? build : Environment.OSVersion.Version.Build;
    }
    private static string? ReadMdmSummary(string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
        if (key is null) return "No effective Update CSP values detected.";
        var names = key.GetValueNames();
        var selected = id.EndsWith("pause-start", StringComparison.Ordinal)
            ? names.Where(static name => name.Contains("Pause", StringComparison.OrdinalIgnoreCase))
            : names.Where(static name => name.Contains("Maintenance", StringComparison.OrdinalIgnoreCase));
        var values = selected.Take(12).Select(name => $"{name}={key.GetValue(name)}").ToArray();
        return values.Length == 0 ? "Not configured." : string.Join("; ", values);
    }
    private static string? ReadMdmValue(PolicyDefinition definition)
    {
        if (definition.RegistryValueName is null) return null;
        var branch = definition.Category == "Delivery Optimization" ? "DeliveryOptimization" : "Update";
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\PolicyManager\current\device\{branch}");
        var alternate = definition.RegistryValueName.StartsWith("DO", StringComparison.OrdinalIgnoreCase)
            ? definition.RegistryValueName[2..]
            : definition.RegistryValueName;
        var name = key?.GetValueNames().FirstOrDefault(candidate =>
            candidate.Equals(definition.RegistryValueName, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(alternate, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Format(key!.GetValue(name));
    }

    private async Task<List<SettingAuditEntry>> ReadAuditCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_auditPath)) return [];
        try
        {
            await using var stream = File.OpenRead(_auditPath);
            return await JsonSerializer.DeserializeAsync<List<SettingAuditEntry>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            File.Move(_auditPath, _auditPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true);
            return [];
        }
    }
    private async Task AppendAuditAsync(IEnumerable<SettingAuditEntry> entries, CancellationToken cancellationToken)
    {
        var all = await ReadAuditCoreAsync(cancellationToken).ConfigureAwait(false);
        all.AddRange(entries);
        if (all.Count > 5000) all = all.OrderByDescending(static entry => entry.ChangedAt).Take(5000).OrderBy(static entry => entry.ChangedAt).ToList();
        await WriteAuditCoreAsync(all, cancellationToken).ConfigureAwait(false);
    }
    private async Task WriteAuditCoreAsync(List<SettingAuditEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
        var temporary = _auditPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _auditPath, true);
    }
    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
