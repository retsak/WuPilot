using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Wua;

public sealed class WuaHistoryProvider : IUpdateHistoryProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<UpdateHistoryRecord>> GetRecentHistoryAsync(int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadHistory(maximumCount, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<UpdateHistoryRecord> ReadHistory(int maximumCount, CancellationToken cancellationToken)
    {
        object? sessionObject = null;
        object? searcherObject = null;
        object? historyObject = null;
        try
        {
            dynamic session = WuaCom.Create("Microsoft.Update.Session");
            sessionObject = session;
            session.ClientApplicationID = "WuPilot Windows Update Workbench";
            dynamic searcher = session.CreateUpdateSearcher();
            searcherObject = searcher;
            var total = Convert.ToInt32(searcher.GetTotalHistoryCount());
            if (total == 0) return [];

            dynamic history = searcher.QueryHistory(0, Math.Min(total, maximumCount));
            historyObject = history;
            var count = Convert.ToInt32(history.Count);
            var records = new List<UpdateHistoryRecord>(count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic entry = history.Item(index);
                string? updateId = null;
                int? revision = null;
                try
                {
                    dynamic identity = entry.UpdateIdentity;
                    updateId = WuaCom.Try<string>(() => identity.UpdateID);
                    revision = WuaCom.Try<int?>(() => identity.RevisionNumber);
                }
                catch (Exception exception) when (exception is COMException or RuntimeBinderException)
                {
                    // Some legacy history entries do not expose an update identity.
                }

                records.Add(new UpdateHistoryRecord(
                    WuaCom.TryDate(() => entry.Date),
                    WuaCom.Try<string>(() => entry.Title),
                    WuaCom.Try<string>(() => entry.Description),
                    updateId,
                    revision,
                    WuaCom.Try<int>(() => entry.Operation),
                    WuaCom.Try<int>(() => entry.ResultCode),
                    WuaCom.Try<int>(() => entry.HResult),
                    WuaCom.Try<string>(() => entry.ClientApplicationID),
                    WuaCom.Try<int?>(() => entry.ServerSelection),
                    WuaCom.Try<string>(() => entry.ServiceID),
                    WuaCom.Try<string>(() => entry.SupportUrl)));
            }

            return records;
        }
        finally
        {
            WuaCom.FinalRelease(historyObject);
            WuaCom.FinalRelease(searcherObject);
            WuaCom.FinalRelease(sessionObject);
        }
    }
}
