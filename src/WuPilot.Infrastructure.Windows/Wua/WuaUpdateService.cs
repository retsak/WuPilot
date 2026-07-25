using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Infrastructure.Windows.Wua;

public sealed class WuaUpdateService(
    IDeviceIdentityProvider identityProvider,
    IInstalledDriverProvider? installedDriverProvider = null) : IUpdateScanService, IUpdateActionService
{
    private const string ClientApplicationId = "WuPilot Windows Update Workbench";
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public async Task<ScanReport> ScanAsync(ScanRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Providers.Count == 0) throw new ArgumentException("Select at least one update provider.", nameof(request));

        var criteria = ScanCriteriaBuilder.Build(request.Preset, request.CustomCriteria);
        var startedAt = DateTimeOffset.Now;
        var device = await identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var providerResults = new List<ProviderScanResult>();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < request.Providers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = request.Providers[index];
                progress?.Report(new OperationProgress(
                    "Scan",
                    $"Scanning {provider.DisplayName} ({index + 1} of {request.Providers.Count})…",
                    index * 100 / request.Providers.Count,
                    provider.Id));

                var result = await Task.Run(
                    () => ScanProvider(provider, criteria, request, progress, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                providerResults.Add(result);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        var mergedUpdates = ScanReportMerger.Merge(providerResults);
        if (installedDriverProvider is not null && mergedUpdates.Any(static update => update.IsDriver))
        {
            try
            {
                progress?.Report(new OperationProgress("Driver evidence", "Correlating offered drivers with locally installed signed drivers…", 95));
                var installedDrivers = await installedDriverProvider.GetInstalledDriversAsync(cancellationToken).ConfigureAwait(false);
                mergedUpdates = DriverEvidenceCorrelator.Enrich(mergedUpdates, installedDrivers);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(new OperationProgress("Driver evidence", $"Installed-driver correlation was unavailable: {exception.Message}", 95));
            }
        }

        var completedAt = DateTimeOffset.Now;
        progress?.Report(new OperationProgress("Complete", "Scan complete.", 100));
        return new ScanReport(
            "1.0",
            Guid.NewGuid(),
            startedAt,
            completedAt,
            criteria,
            device,
            providerResults,
            mergedUpdates);
    }

    public async Task<UpdateActionResult> ExecuteAsync(UpdateActionRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ExecuteCore(request, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static ProviderScanResult ScanProvider(
        UpdateProviderDefinition provider,
        string criteria,
        ScanRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        object? sessionObject = null;
        object? searcherObject = null;
        object? resultObject = null;
        object? serviceManagerObject = null;
        object? scanPackageServiceObject = null;
        string? scanPackageServiceId = null;
        try
        {
            dynamic session = WuaCom.Create("Microsoft.Update.Session");
            sessionObject = session;
            session.ClientApplicationID = ClientApplicationId;
            var effectiveProvider = provider;
            if (!string.IsNullOrWhiteSpace(provider.ScanPackagePath))
            {
                if (!File.Exists(provider.ScanPackagePath))
                {
                    throw new FileNotFoundException("The offline scan package was not found.", provider.ScanPackagePath);
                }

                dynamic serviceManager = WuaCom.Create("Microsoft.Update.ServiceManager");
                serviceManagerObject = serviceManager;
                dynamic scanPackageService = serviceManager.AddScanPackageService("WuPilot Offline Scan", provider.ScanPackagePath, 0);
                scanPackageServiceObject = scanPackageService;
                scanPackageServiceId = Convert.ToString(scanPackageService.ServiceID);
                effectiveProvider = provider with { ServiceId = scanPackageServiceId };
            }

            if (effectiveProvider.ServerSelection == UpdateServerSelection.Others &&
                !string.IsNullOrWhiteSpace(effectiveProvider.ServiceId) &&
                provider.ScanPackagePath is null)
            {
                dynamic serviceManager = WuaCom.Create("Microsoft.Update.ServiceManager");
                serviceManagerObject = serviceManager;
                EnsureServiceRegistered(serviceManager, effectiveProvider);
            }

            dynamic searcher = session.CreateUpdateSearcher();
            searcherObject = searcher;
            ConfigureSearcher(searcher, effectiveProvider, request.Online && provider.ScanPackagePath is null, request.IncludePotentiallySuperseded);

            dynamic result = searcher.Search(criteria);
            resultObject = result;
            var warnings = ReadWarnings(result);
            var count = Convert.ToInt32(result.Updates.Count);
            var updates = new List<UpdateRecord>(count);

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic update = result.Updates.Item(index);
                updates.Add(WuaUpdateMapper.Map((object)update, provider));
                progress?.Report(new OperationProgress(
                    "Read results",
                    $"{provider.DisplayName}: reading update {index + 1} of {count}",
                    count == 0 ? null : (index + 1) * 100 / count,
                    provider.Id));
            }

            return new ProviderScanResult(
                provider,
                startedAt,
                DateTimeOffset.Now,
                WuaCom.Try<int>(() => result.ResultCode),
                warnings,
                updates);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException or RuntimeBinderException or IOException)
        {
            var hResult = exception.HResult;
            var explanation = HResultCatalog.Explain(hResult);
            return new ProviderScanResult(
                provider,
                startedAt,
                DateTimeOffset.Now,
                4,
                [],
                [],
                explanation.Code,
                $"{exception.Message} {explanation.Recommendation}");
        }
        finally
        {
            WuaCom.FinalRelease(resultObject);
            WuaCom.FinalRelease(searcherObject);
            if (serviceManagerObject is not null && scanPackageServiceId is not null)
            {
                try
                {
                    ((dynamic)serviceManagerObject).RemoveService(scanPackageServiceId);
                }
                catch (Exception exception) when (exception is COMException or RuntimeBinderException)
                {
                    // flags=0 also makes the registration volatile; releasing the service removes it.
                }
            }
            WuaCom.FinalRelease(scanPackageServiceObject);
            WuaCom.FinalRelease(serviceManagerObject);
            WuaCom.FinalRelease(sessionObject);
        }
    }

    private static UpdateActionResult ExecuteCore(
        UpdateActionRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Provider.ScanPackagePath is not null)
        {
            return Failure(request, unchecked((int)0x8024000C), "Offline scan packages contain security applicability metadata only; they cannot download, install, hide, or show updates.");
        }

        object? sessionObject = null;
        object? searcherObject = null;
        object? resultObject = null;
        object? collectionObject = null;
        object? serviceManagerObject = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress("Locate", "Revalidating update applicability…", 10, request.Provider.Id));
            dynamic session = WuaCom.Create("Microsoft.Update.Session");
            sessionObject = session;
            session.ClientApplicationID = ClientApplicationId;
            if (request.Provider.ServerSelection == UpdateServerSelection.Others &&
                !string.IsNullOrWhiteSpace(request.Provider.ServiceId))
            {
                dynamic serviceManager = WuaCom.Create("Microsoft.Update.ServiceManager");
                serviceManagerObject = serviceManager;
                EnsureServiceRegistered(serviceManager, request.Provider);
            }
            dynamic searcher = session.CreateUpdateSearcher();
            searcherObject = searcher;
            ConfigureSearcher(searcher, request.Provider, online: true, includeSuperseded: true);
            dynamic result = searcher.Search(ScanCriteriaBuilder.ForIdentity(request.Update.UpdateId, request.Update.RevisionNumber));
            resultObject = result;
            if (Convert.ToInt32(result.Updates.Count) == 0)
            {
                return Failure(request, unchecked((int)0x80240017), "The update is no longer applicable from this provider.");
            }

            dynamic update = result.Updates.Item(0);
            if (request.Action is UpdateAction.Hide or UpdateAction.Show)
            {
                update.IsHidden = request.Action == UpdateAction.Hide;
                return Success(request, $"Update is now {(request.Action == UpdateAction.Hide ? "hidden" : "visible")}.");
            }

            if (!WuaCom.Try<bool>(() => update.EulaAccepted) && !request.AcceptEula)
            {
                return Failure(request, unchecked((int)0x80240022), "The update license terms have not been accepted.");
            }

            if (!WuaCom.Try<bool>(() => update.EulaAccepted) && request.AcceptEula)
            {
                update.AcceptEula();
            }

            if (WuaCom.Try<bool>(() => update.InstallationBehavior.CanRequestUserInput))
            {
                return Failure(request, unchecked((int)0x80240020), "This update can request user input and is not safe for unattended installation.");
            }

            dynamic collection = WuaCom.Create("Microsoft.Update.UpdateColl");
            collectionObject = collection;
            collection.Add(update);

            if (!WuaCom.Try<bool>(() => update.IsDownloaded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress("Download", "Downloading update payload…", 40, request.Provider.Id));
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = collection;
                dynamic downloadResult = downloader.Download();
                var downloadResultCode = WuaCom.Try<int>(() => downloadResult.ResultCode);
                var downloadHResult = WuaCom.Try<int>(() => downloadResult.HResult);
                if (downloadResultCode is not (2 or 3))
                {
                    return new UpdateActionResult(request.Action, request.Update.UpdateId, downloadResultCode, downloadHResult, false, "Download failed.", DateTimeOffset.Now);
                }
            }

            if (request.Action == UpdateAction.Download)
            {
                return Success(request, "Update downloaded successfully.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress("Install", "Installing update…", 75, request.Provider.Id));
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic installResult = installer.Install();
            var resultCode = WuaCom.Try<int>(() => installResult.ResultCode);
            var hResult = WuaCom.Try<int>(() => installResult.HResult);
            var rebootRequired = WuaCom.Try<bool>(() => installResult.RebootRequired);
            var explanation = hResult == 0 ? null : HResultCatalog.Explain(hResult);
            return new UpdateActionResult(
                request.Action,
                request.Update.UpdateId,
                resultCode,
                hResult,
                rebootRequired,
                resultCode is 2 or 3 ? "Installation completed." : $"Installation failed. {explanation?.Explanation} {explanation?.Recommendation}",
                DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            return Failure(request, exception.HResult, $"{exception.Message} {HResultCatalog.Explain(exception.HResult).Recommendation}");
        }
        finally
        {
            WuaCom.FinalRelease(collectionObject);
            WuaCom.FinalRelease(resultObject);
            WuaCom.FinalRelease(searcherObject);
            WuaCom.FinalRelease(serviceManagerObject);
            WuaCom.FinalRelease(sessionObject);
        }
    }

    private static void EnsureServiceRegistered(dynamic serviceManager, UpdateProviderDefinition provider)
    {
        object? servicesObject = null;
        try
        {
            dynamic services = serviceManager.Services;
            servicesObject = services;
            var count = Convert.ToInt32(services.Count);
            for (var index = 0; index < count; index++)
            {
                object? serviceObject = null;
                try
                {
                    dynamic service = services.Item(index);
                    serviceObject = service;
                    var serviceId = Convert.ToString(service.ServiceID);
                    if (string.Equals(serviceId, provider.ServiceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                finally
                {
                    WuaCom.FinalRelease(serviceObject);
                }
            }
        }
        finally
        {
            WuaCom.FinalRelease(servicesObject);
        }

        throw new COMException(
            $"{provider.DisplayName} is not registered with Windows Update Agent. WuPilot did not register it because scans are read-only.",
            unchecked((int)0x80248014));
    }

    private static void ConfigureSearcher(dynamic searcher, UpdateProviderDefinition provider, bool online, bool includeSuperseded)
    {
        searcher.ClientApplicationID = ClientApplicationId;
        searcher.Online = online;
        searcher.IncludePotentiallySupersededUpdates = includeSuperseded;
        searcher.ServerSelection = (int)provider.ServerSelection;
        if (provider.ServerSelection == UpdateServerSelection.Others && !string.IsNullOrWhiteSpace(provider.ServiceId))
        {
            searcher.ServiceID = provider.ServiceId;
        }
    }

    private static IReadOnlyList<string> ReadWarnings(dynamic result)
    {
        var warnings = new List<string>();
        try
        {
            var count = Convert.ToInt32(result.Warnings.Count);
            for (var index = 0; index < count; index++)
            {
                dynamic warning = result.Warnings.Item(index);
                warnings.Add($"0x{unchecked((uint)Convert.ToInt32(warning.HResult)):X8}: {Convert.ToString(warning.Message)}");
            }
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            // Warning metadata is optional.
        }

        return warnings;
    }

    private static UpdateActionResult Success(UpdateActionRequest request, string message) =>
        new(request.Action, request.Update.UpdateId, 2, 0, false, message, DateTimeOffset.Now);

    private static UpdateActionResult Failure(UpdateActionRequest request, int hResult, string message) =>
        new(request.Action, request.Update.UpdateId, 4, hResult, false, message, DateTimeOffset.Now);
}
