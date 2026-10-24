using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;

namespace FastDbExplorer.Wpf.ViewModels;

/// <summary>
/// Live monitor layers: each monitor with the live-map option owns ONE map layer (its id = the monitor id) that
/// collects its matching records. A record keeps its point across checks (same key = same record, updated, not added
/// again). Everything here runs on the UI thread.
/// </summary>
public sealed partial class MapViewModel
{
    /// <summary>Upper bound per monitor; the oldest records leave the map first.</summary>
    public const int MaxLivePointsPerMonitor = 2000;

    private readonly Dictionary<Guid, LiveMonitorPoints> _liveMonitors = [];

    private sealed class LiveMonitorPoints
    {
        public Dictionary<string, MapPoint> ByKey { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = [];
    }

    /// <summary>Adds or updates the matching records of one monitor check and redraws that monitor's layer.</summary>
    public void UpsertLiveMonitorPoints(Guid monitorId, string monitorName, IReadOnlyList<LiveMapPoint> points)
    {
        if (points.Count == 0) return;
        if (!_liveMonitors.TryGetValue(monitorId, out var live))
        {
            live = new LiveMonitorPoints();
            _liveMonitors[monitorId] = live;
        }

        foreach (var point in points)
        {
            var name = string.IsNullOrWhiteSpace(point.Name) ? "Record" : point.Name;
            var id = live.ByKey.TryGetValue(point.Key, out var existing) ? existing.Id : Guid.NewGuid();
            live.ByKey[point.Key] = new MapPoint(id, name, point.Latitude, point.Longitude, monitorId, point.Details);
            if (existing is null) live.Order.Add(point.Key);
        }

        while (live.Order.Count > MaxLivePointsPerMonitor)
        {
            live.ByKey.Remove(live.Order[0]);
            live.Order.RemoveAt(0);
        }

        PublishLiveMonitorLayer(monitorId, monitorName, live);
    }

    /// <summary>Removes a monitor's live layer and its points from the map.</summary>
    public void RemoveLiveMonitorLayer(Guid monitorId) => RemoveQueryResult(monitorId);

    private void PublishLiveMonitorLayer(Guid monitorId, string monitorName, LiveMonitorPoints live)
    {
        var name = string.IsNullOrWhiteSpace(monitorName) ? "monitor" : monitorName;
        var points = live.Order.Select(key => live.ByKey[key]).ToList();
        var layer = new MapLayer(monitorId, MonitoringStrings.LiveLayerName(name), name, points);
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
        QueryResultAdded?.Invoke(set);       // first point of this monitor: the map zooms to it once
    }
}
