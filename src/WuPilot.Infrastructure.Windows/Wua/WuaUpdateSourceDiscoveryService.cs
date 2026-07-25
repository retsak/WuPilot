using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Wua;

public sealed class WuaUpdateSourceDiscoveryService : IUpdateSourceDiscoveryService
{
    public Task<IReadOnlyList<UpdateSourceRegistration>> GetRegisteredSourcesAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadRegisteredSources(cancellationToken), cancellationToken);

    private static IReadOnlyList<UpdateSourceRegistration> ReadRegisteredSources(CancellationToken cancellationToken)
    {
        object? managerObject = null;
        object? servicesObject = null;
        try
        {
            dynamic manager = WuaCom.Create("Microsoft.Update.ServiceManager");
            managerObject = manager;
            dynamic services = manager.Services;
            servicesObject = services;
            var count = Convert.ToInt32(services.Count);
            var registrations = new List<UpdateSourceRegistration>(count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? serviceObject = null;
                try
                {
                    dynamic service = services.Item(index);
                    serviceObject = service;
                    registrations.Add(new UpdateSourceRegistration(
                        Convert.ToString(service.Name) ?? "Unnamed WUA service",
                        Convert.ToString(service.ServiceID) ?? string.Empty,
                        WuaCom.Try<bool>(() => service.IsManaged),
                        WuaCom.Try<bool>(() => service.IsDefaultAUService),
                        WuaCom.Try<bool>(() => service.IsScanPackageService),
                        WuaCom.Try<bool>(() => service.OffersWindowsUpdates)));
                }
                finally
                {
                    WuaCom.FinalRelease(serviceObject);
                }
            }

            return registrations
                .OrderByDescending(static source => source.IsDefaultAuService)
                .ThenByDescending(static source => source.OffersWindowsUpdates)
                .ThenBy(static source => source.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            WuaCom.FinalRelease(servicesObject);
            WuaCom.FinalRelease(managerObject);
        }
    }
}
