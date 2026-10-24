using System.Windows.Threading;
using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;

namespace FastDbExplorer.Wpf.ViewModels;

/// <summary>
/// Live monitor layers: each monitor with the live-map option owns ONE map layer (its id = the monitor id) that collects
/// its matching records. Same key = same record: it is updated, never added twice. Display time (see
/// <see cref="LiveResultLifetime"/>): a record is shown from the check that found it, a later check that finds it again
/// starts its time over, and an expired record leaves the map only; the database and the Excel layers are untouched.
/// Everything here runs on the UI thread.
/// </summary>
public sealed partial class MapViewModel
{
    /// <summary>Upper bound per monitor; the oldest records leave the map first.</summary>
    public const int MaxLivePointsPerMonitor = 2000;

    /// <summary>How often expired live results are looked for. Display times are whole minutes, so this is plenty.</summary>
    private static readonly TimeSpan LiveExpiryInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<Guid, LiveMonitorPoints> _liveMonitors = [];
    private DispatcherTimer? _liveExpiryTimer;

    private sealed class LiveMonitorPoints
    {
        public string Name { get; set; } = "monitor";
        public int Minutes { get; set; } = LiveResultLifetime.DefaultMinutes;
        public Dictionary<string, LivePoint> ByKey { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = [];
    }

    private sealed record LivePoint(MapPoint Point, DateTime SeenUtc);

    /// <summary>Adds or updates the matching records of one monitor check and redraws that monitor's layer.</summary>
    public void UpsertLiveMonitorPoints(Guid monitorId, string monitorName, int displayMinutes, IReadOnlyList<LiveMapPoint> points)
    {
        if (points.Count == 0) return;

        var now = DateTime.UtcNow;
        if (!_liveMonitors.TryGetValue(monitorId, out var live))
        {
            live = new LiveMonitorPoints();
            _liveMonitors[monitorId] = live;
        }
        live.Name = string.IsNullOrWhiteSpace(monitorName) ? "monitor" : monitorName;
        live.Minutes = LiveResultLifetime.Normalize(displayMinutes);

        foreach (var point in points)
        {
            var name = string.IsNullOrWhiteSpace(point.Name) ? "Record" : point.Name;
            var known = live.ByKey.TryGetValue(point.Key, out var existing);
            var id = known ? existing!.Point.Id : Guid.NewGuid();
            // The same record again: updated in place, and its display time starts over (renewed).
            live.ByKey[point.Key] = new LivePoint(
                new MapPoint(id, name, point.Latitude, point.Longitude, monitorId, point.Details), now);
            if (!known) live.Order.Add(point.Key);
        }

        while (live.Order.Count > MaxLivePointsPerMonitor)
        {
            live.ByKey.Remove(live.Order[0]);
            live.Order.RemoveAt(0);
        }

        PublishLiveMonitorLayer(monitorId, live);
        EnsureExpiryTimer();
    }

    /// <summary>Removes a monitor's live layer and its points from the map (the database and Excel layers stay).</summary>
    public void RemoveLiveMonitorLayer(Guid monitorId) => RemoveQueryResult(monitorId);

    /// <summary>Removes the live results whose display time is over. A monitor left with none leaves the map.</summary>
    private void ExpireLiveMonitorPoints(DateTime nowUtc)
    {
        foreach (var (monitorId, live) in _liveMonitors.ToList())
        {
            var expired = live.Order
                .Where(key => LiveResultLifetime.IsExpired(live.ByKey[key].SeenUtc, nowUtc, live.Minutes))
                .ToList();
            if (expired.Count == 0) continue;

            foreach (var key in expired)
            {
                live.ByKey.Remove(key);
                live.Order.Remove(key);
            }

            if (live.Order.Count == 0) RemoveQueryResult(monitorId);
            else PublishLiveMonitorLayer(monitorId, live);
        }

        StopExpiryTimerIfIdle();
    }

    private void EnsureExpiryTimer()
    {
        if (_liveExpiryTimer is not null) return;
        _liveExpiryTimer = new DispatcherTimer { Interval = LiveExpiryInterval };
        _liveExpiryTimer.Tick += (_, _) => ExpireLiveMonitorPoints(DateTime.UtcNow);
        _liveExpiryTimer.Start();
    }

    private void StopExpiryTimerIfIdle()
    {
        if (_liveExpiryTimer is null || _liveMonitors.Count > 0) return;
        _liveExpiryTimer.Stop();
        _liveExpiryTimer = null;
    }

    private void PublishLiveMonitorLayer(Guid monitorId, LiveMonitorPoints live)
    {
        var points = live.Order.Select(key => live.ByKey[key].Point).ToList();
        var layer = new MapLayer(monitorId, MonitoringStrings.LiveLayerName(live.Name), live.Name, points);
        var set = new QueryResultSet(layer, MapLayerKind.Monitoring);

        var index = _queryResults.FindIndex(s => s.Layer.Id == monitorId);
        if (index >= 0)
        {
            _queryResults[index] = set;      // same id: the view redraws this layer in place
            CoordinateLayerChanged?.Invoke(layer);
            return;
        }

        _queryResults.Add(set);
        NotifyQueryResultsChanged();
        QueryResultAdded?.Invoke(set);       // the first point of this monitor: the map zooms to it once
    }
}
