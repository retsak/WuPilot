using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Wua;

internal static class WuaUpdateMapper
{
    public static UpdateRecord Map(object updateObject, UpdateProviderDefinition provider)
    {
        dynamic update = updateObject;
        dynamic identity = update.Identity;
        var updateId = Convert.ToString(identity.UpdateID) ?? throw new COMException("WUA returned an update without an UpdateID.");
        var revision = Convert.ToInt32(identity.RevisionNumber);
        var type = WuaCom.Try<int>(() => update.Type, 0);
        var kind = type switch
        {
            1 => UpdateKind.Software,
            2 => UpdateKind.Driver,
            _ => UpdateKind.Unknown
        };

        var (categoryNames, categoryIds) = ReadCategories(updateObject);
        var driver = kind == UpdateKind.Driver ? ReadDriver(updateObject) : null;

        return new UpdateRecord(
            updateId,
            revision,
            WuaCom.Try<string>(() => update.Title, "Untitled update") ?? "Untitled update",
            WuaCom.Try<string>(() => update.Description),
            kind,
            new[] { provider.Id },
            new[] { provider.DisplayName },
            provider.Id,
            WuaCom.ReadStringCollection(() => update.KBArticleIDs),
            WuaCom.ReadStringCollection(() => update.CveIDs),
            categoryNames,
            categoryIds,
            WuaCom.Try<string>(() => update.MsrcSeverity),
            WuaCom.Try<long?>(() => update.MinDownloadSize),
            WuaCom.Try<long?>(() => update.MaxDownloadSize),
            WuaCom.Try<bool>(() => update.IsInstalled),
            WuaCom.Try<bool>(() => update.IsDownloaded),
            WuaCom.Try<bool>(() => update.IsHidden),
            WuaCom.Try<bool>(() => update.IsMandatory),
            WuaCom.Try<bool>(() => update.IsUninstallable),
            WuaCom.Try<bool>(() => update.EulaAccepted),
            WuaCom.Try<bool?>(() => update.RebootRequired),
            WuaCom.Try<bool?>(() => update.IsPresent),
            WuaCom.Try<bool?>(() => update.BrowseOnly) ?? driver?.BrowseOnly,
            WuaCom.TryDate(() => update.LastDeploymentChangeTime),
            WuaCom.Try<string>(() => update.SupportUrl),
            WuaCom.Try<string>(() => update.ReleaseNotes),
            WuaCom.Try<int?>(() => update.DeploymentAction),
            WuaCom.Try<int?>(() => update.DownloadPriority),
            WuaCom.Try<int?>(() => update.InstallationBehavior.Impact),
            WuaCom.Try<int?>(() => update.InstallationBehavior.RebootBehavior),
            WuaCom.Try<bool?>(() => update.InstallationBehavior.CanRequestUserInput),
            driver);
    }

    private static (IReadOnlyList<string> Names, IReadOnlyList<string> Ids) ReadCategories(object updateObject)
    {
        dynamic update = updateObject;
        var names = new List<string>();
        var ids = new List<string>();
        try
        {
            dynamic categories = update.Categories;
            var count = Convert.ToInt32(categories.Count);
            for (var index = 0; index < count; index++)
            {
                dynamic category = categories.Item(index);
                var name = WuaCom.Try<string>(() => category.Name);
                var id = WuaCom.Try<string>(() => category.CategoryID);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            // Keep the update even if category metadata is unavailable.
        }

        return (names, ids);
    }

    private static DriverMetadata ReadDriver(object updateObject)
    {
        dynamic update = updateObject;
        var entries = new List<DriverEntry>();
        try
        {
            dynamic collection = update.WindowsDriverUpdateEntries;
            var count = Convert.ToInt32(collection.Count);
            for (var index = 0; index < count; index++)
            {
                dynamic entry = collection.Item(index);
                entries.Add(new DriverEntry(
                    WuaCom.Try<string>(() => entry.DriverManufacturer),
                    WuaCom.Try<string>(() => entry.DriverProvider),
                    WuaCom.Try<string>(() => entry.DriverModel),
                    WuaCom.Try<string>(() => entry.DriverClass),
                    WuaCom.Try<string>(() => entry.DriverHardwareID),
                    WuaCom.TryDate(() => entry.DriverVerDate),
                    WuaCom.Try<int?>(() => entry.DeviceProblemNumber),
                    WuaCom.Try<int?>(() => entry.DeviceStatus)));
            }
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            // IWindowsDriverUpdate4 is not guaranteed on every supported client.
        }

        return new DriverMetadata(
            WuaCom.Try<string>(() => update.DriverManufacturer),
            WuaCom.Try<string>(() => update.DriverProvider),
            WuaCom.Try<string>(() => update.DriverModel),
            WuaCom.Try<string>(() => update.DriverClass),
            WuaCom.Try<string>(() => update.DriverHardwareID),
            WuaCom.TryDate(() => update.DriverVerDate),
            WuaCom.Try<int?>(() => update.DeviceProblemNumber),
            WuaCom.Try<int?>(() => update.DeviceStatus),
            WuaCom.Try<bool?>(() => update.BrowseOnly),
            WuaCom.Try<bool?>(() => update.PerUser),
            entries);
    }
}
