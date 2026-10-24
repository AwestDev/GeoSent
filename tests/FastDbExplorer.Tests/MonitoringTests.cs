using System.IO.Compression;
using System.Xml.Linq;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Infrastructure;
using FastDbExplorer.Infrastructure.Monitoring;
using Xunit;

namespace FastDbExplorer.Tests;

public class MonitoringTests
{
    private static readonly ConnectionSettings Settings = new("srv", AuthenticationMode.Windows, null, null, true);

    private static readonly IReadOnlyList<ColumnInfo> Columns =
    [
        new ColumnInfo("Id", "int", 1),
        new ColumnInfo("Place", "nvarchar", 0)
    ];

    private static MonitorDefinition Monitor(string? last = null, DateTime? checkedAt = null, int minutes = 60, bool enabled = true) =>
        new(Guid.NewGuid(), "m", "srv", "db", "dbo", "T", "Id",
            [new FilterCondition("Place", FilterOperator.EqualTo, "A")], FilterLogic.And, minutes, Path.GetTempPath(), false, enabled,
            last, checkedAt);

    [Fact]
    public void Required_filters_are_and_ed_with_user_filters()
    {
        var request = new PageRequest("db", "dbo", "T", ["Id", "Place"],
            [new FilterCondition("Place", FilterOperator.EqualTo, "A"), new FilterCondition("Place", FilterOperator.EqualTo, "B")],
            FilterLogic.Or, ["Id"], false, 100, 0, null,
            [new FilterCondition("Id", FilterOperator.GreaterThan, "10"), new FilterCondition("Id", FilterOperator.LessOrEqual, "20")]);

        var built = SelectQueryBuilder.Build(request, Columns);

        Assert.Contains("WHERE ([Place] = @p0 OR [Place] = @p1) AND [Id] > @p2 AND [Id] <= @p3 ORDER BY", built.Sql);
        Assert.Equal(10, (int)built.Parameters.Single(p => p.Name == "@p2").Value);
        Assert.Equal(20, (int)built.Parameters.Single(p => p.Name == "@p3").Value);
    }

    [Fact]
    public void Watermark_comparer_compares_numbers_and_dates_not_text()
    {
        Assert.True(WatermarkComparer.Compare("100", "99", "int") > 0); // as text "100" < "99"
        Assert.True(WatermarkComparer.Compare("2026-10-05 08:00:00.0000000", "2026-10-05 07:59:59.0000000", "datetime2") > 0);
        Assert.Equal(0, WatermarkComparer.Compare("5", "5", "bigint"));
    }

    [Fact]
    public void IsDue_follows_enabled_flag_interval_and_last_attempt()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(MonitorScheduler.IsDue(Monitor(), now, null));
        Assert.False(MonitorScheduler.IsDue(Monitor(enabled: false), now, null));
        Assert.False(MonitorScheduler.IsDue(Monitor(checkedAt: now.UtcDateTime.AddMinutes(-30)), now, null));
        Assert.True(MonitorScheduler.IsDue(Monitor(checkedAt: now.UtcDateTime.AddMinutes(-61)), now, null));
        // a recent attempt (even a failed one) postpones the next run by a full interval
        Assert.False(MonitorScheduler.IsDue(Monitor(checkedAt: now.UtcDateTime.AddHours(-2)), now, now.AddMinutes(-5)));
    }

    [Fact]
    public async Task Xlsx_is_a_valid_package_with_expected_cells()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fdbx-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await new XlsxReportWriter().WriteAsync(
                "پایش/تست", new DateTime(2026, 10, 5, 8, 30, 0, DateTimeKind.Utc), ["Id", "Name", "When"],
                [[1, "علی & <x>", new DateTime(2026, 1, 2, 3, 4, 5)], [2, null, null]], dir, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.EndsWith(".xlsx", path);
            Assert.DoesNotContain('/', Path.GetFileName(path));

            using var zip = ZipFile.OpenRead(path);
            foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
                Assert.NotNull(zip.GetEntry(name));

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(sheet).Descendants(ns + "c").ToList();

            Assert.Contains(cells, c => (string?)c.Attribute("r") == "A1" && c.Value == "Id");
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "A2" && c.Value == "1");
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "B2" && c.Value == "علی & <x>");
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "C2" && c.Value == "2026-01-02 03:04:05");
            Assert.DoesNotContain(cells, c => (string?)c.Attribute("r") == "B3"); // NULL = empty cell
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_xlsx_writer_streams_async_rows_to_the_selected_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "fdbx-" + Guid.NewGuid().ToString("N"), "query.xlsx");
        try
        {
            static async IAsyncEnumerable<object?[]> Rows()
            {
                yield return [1, "first"];
                await Task.Yield();
                yield return [2, "long & <value>"];
            }

            await new XlsxReportWriter().WriteQueryAsync(["Id", "Value"], Rows(), path, CancellationToken.None);

            Assert.True(File.Exists(path));
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(sheet).Descendants(ns + "c").ToList();
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "A2" && c.Value == "1");
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "B3" && c.Value == "long & <value>");
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Store_round_trips_a_monitor()
    {
        var path = Path.Combine(Path.GetTempPath(), "fdbx-" + Guid.NewGuid().ToString("N"), "monitors.json");
        try
        {
            var store = new JsonMonitorStore(path);
            var layerId = Guid.NewGuid();
            var original = Monitor("42", new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), 120) with
            {
                SettleSeconds = 45,
                CoordinateLayerId = layerId,
                LatitudeColumn = "Lat",
                LongitudeColumn = "Lon",
                DistanceMeters = 20_000
            };
            await store.SaveAsync([original]);

            var loaded = (await store.LoadAsync()).Single();
            Assert.Equal(original.Id, loaded.Id);
            Assert.Equal(120, loaded.IntervalMinutes);
            Assert.Equal(45, loaded.SettleSeconds);
            Assert.Equal("42", loaded.LastWatermark);
            Assert.Equal(FilterOperator.EqualTo, loaded.Conditions.Single().Operator);
            Assert.Equal(FilterLogic.And, loaded.Logic);
            Assert.Equal(layerId, loaded.CoordinateLayerId);
            Assert.Equal("Lat", loaded.LatitudeColumn);
            Assert.Equal("Lon", loaded.LongitudeColumn);
            Assert.Equal(20_000d, loaded.DistanceMeters);
            Assert.DoesNotContain("password", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Monitor_files_without_proximity_fields_load_with_the_rule_disabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "fdbx-" + Guid.NewGuid().ToString("N"), "monitors.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "{\"Version\":1,\"Monitors\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"legacy\",\"Server\":\"srv\",\"Database\":\"db\",\"Schema\":\"dbo\",\"Table\":\"T\",\"WatermarkColumn\":\"Id\",\"Conditions\":[],\"Logic\":\"And\",\"IntervalMinutes\":60,\"OutputFolder\":\"C:/reports\",\"PlaySound\":false,\"Enabled\":true}]} ");

            var loaded = Assert.Single(await new JsonMonitorStore(path).LoadAsync());

            Assert.False(loaded.HasProximityRule);
            Assert.Null(loaded.CoordinateLayerId);
            Assert.Null(loaded.LatitudeColumn);
            Assert.Null(loaded.LongitudeColumn);
            Assert.Null(loaded.DistanceMeters);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Haversine_proximity_includes_inside_and_exact_boundary_but_excludes_outside()
    {
        var boundary = MonitorProximity.HaversineMeters(0, 0, 0, 0.1);

        Assert.True(MonitorProximity.IsWithinDistance(0, 0, 0, 0.1, boundary + 0.01));
        Assert.True(MonitorProximity.IsWithinDistance(0, 0, 0, 0.1, boundary));
        Assert.False(MonitorProximity.IsWithinDistance(0, 0, 0, 0.1, boundary - 0.01));
    }

    [Fact]
    public async Task First_check_only_records_the_starting_point()
    {
        var metadata = new FakeMetadata(Columns, new PageResult(["Id", "Place"], [], false, null));
        var service = new MonitorCheckService(metadata, new FakeWatermark("500"), new FakeReports(), new FakeLayers());

        var result = await service.CheckAsync(Settings, Monitor(), CancellationToken.None);

        Assert.True(result.IsBaseline);
        Assert.Equal("500", result.NewWatermark);
        Assert.Empty(result.Rows);
        Assert.Equal(0, metadata.PageCalls);
    }

    [Fact]
    public async Task New_matching_rows_are_reported_and_the_watermark_moves_to_the_window_end()
    {
        var rows = new List<object?[]> { new object?[] { 11, "A" }, new object?[] { 12, "A" } };
        var metadata = new FakeMetadata(Columns, new PageResult(["Id", "Place"], rows, false, null));
        var reports = new FakeReports();
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), reports, new FakeLayers());

        var result = await service.CheckAsync(Settings, Monitor("10", DateTime.UtcNow.AddHours(-2)), CancellationToken.None);

        Assert.False(result.IsBaseline);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("12", result.NewWatermark);
        Assert.Equal("fake.xlsx", result.ReportPath);
        Assert.Equal(2, reports.RowCount);

        var required = metadata.LastRequest!.RequiredFilters!;
        Assert.Equal(FilterOperator.GreaterThan, required[0].Operator);
        Assert.Equal("10", required[0].Value);
        Assert.Equal(FilterOperator.LessOrEqual, required[1].Operator);
        Assert.Equal("12", required[1].Value);
    }

    [Fact]
    public async Task Nothing_new_means_no_query_and_no_report()
    {
        var metadata = new FakeMetadata(Columns, new PageResult(["Id", "Place"], [], false, null));
        var reports = new FakeReports();
        var service = new MonitorCheckService(metadata, new FakeWatermark("10"), reports, new FakeLayers());

        var result = await service.CheckAsync(Settings, Monitor("10", DateTime.UtcNow.AddHours(-2)), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Null(result.ReportPath);
        Assert.Equal("10", result.NewWatermark);
        Assert.Equal(0, metadata.PageCalls);
        Assert.Equal(0, reports.Calls);
    }

    [Fact]
    public async Task Reaching_the_row_cap_stops_at_the_next_watermark_group()
    {
        var metadata = new FakeMetadata(Columns, request =>
        {
            var rows = Enumerable.Range(0, MonitorCheckService.PageSize)
                .Select(i => new object?[] { (int)request.Offset + i + 1, "A" }).ToList();
            return new PageResult(["Id", "Place"], rows, true, null);
        });
        var service = new MonitorCheckService(metadata, new FakeWatermark("999999"), new FakeReports(), new FakeLayers());

        var result = await service.CheckAsync(Settings, Monitor("0", DateTime.UtcNow.AddHours(-1)), CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(MonitorCheckService.MaxRows / MonitorCheckService.PageSize + 1, metadata.PageCalls); // probe the next watermark group
        Assert.Equal(MonitorCheckService.MaxRows.ToString(), result.NewWatermark);
    }

    [Fact]
    public async Task Row_cap_refuses_to_advance_through_a_duplicate_watermark_group()
    {
        var watermark = new DateTime(2026, 10, 5, 8, 0, 0);
        var columns = new[]
        {
            new ColumnInfo("Id", "int", 1),
            new ColumnInfo("Place", "nvarchar", 0),
            new ColumnInfo("Created", "datetime2", 0)
        };
        var metadata = new FakeMetadata(columns, request =>
        {
            var start = (int)request.Offset + 1;
            var count = request.Offset < MonitorCheckService.MaxRows ? MonitorCheckService.PageSize : 1;
            var rows = Enumerable.Range(start, count).Select(id => new object?[] { id, "A", watermark }).ToList();
            return new PageResult(["Id", "Place", "Created"], rows, true, null);
        });
        var monitor = Monitor(WatermarkTypes.Format(watermark.AddDays(-1)), DateTime.UtcNow.AddHours(-1)) with
        {
            WatermarkColumn = "Created"
        };
        var service = new MonitorCheckService(metadata, new FakeWatermark(WatermarkTypes.Format(watermark)), new FakeReports(), new FakeLayers());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(Settings, monitor, CancellationToken.None));

        Assert.True(error.Message.Contains("same watermark value", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MonitorCheckService.MaxRows / MonitorCheckService.PageSize + 1, metadata.PageCalls);
    }

    [Fact]
    public async Task Table_that_was_empty_at_the_first_check_counts_everything_later_as_new()
    {
        var rows = new List<object?[]> { new object?[] { 1, "A" } };
        var metadata = new FakeMetadata(Columns, new PageResult(["Id", "Place"], rows, false, null));
        var service = new MonitorCheckService(metadata, new FakeWatermark("5"), new FakeReports(), new FakeLayers());

        // LastWatermark is null but a check already succeeded (LastCheckedUtc set): the table was empty back then.
        var result = await service.CheckAsync(Settings, Monitor(null, DateTime.UtcNow.AddHours(-1)), CancellationToken.None);

        Assert.False(result.IsBaseline);
        Assert.Single(result.Rows);
        var required = metadata.LastRequest!.RequiredFilters!;
        Assert.Single(required);
        Assert.Equal(FilterOperator.LessOrEqual, required[0].Operator);
    }

    [Fact]
    public void Settle_holds_back_the_newest_seconds_of_a_date_column_but_never_a_number_column()
    {
        var now = new DateTime(2026, 10, 5, 12, 0, 0);
        var atNow = new WatermarkReading(WatermarkTypes.Format(now), now);
        var older = new WatermarkReading(WatermarkTypes.Format(now.AddMinutes(-5)), now);

        Assert.Equal(WatermarkTypes.Format(now.AddSeconds(-30)), MonitorCheckService.SettleUpper(atNow, "datetime2", 30));
        Assert.Equal(older.Max, MonitorCheckService.SettleUpper(older, "datetime2", 30)); // already older than the settle time
        Assert.Equal(atNow.Max, MonitorCheckService.SettleUpper(atNow, "datetime2", 0)); // settle disabled
        Assert.Equal("100", MonitorCheckService.SettleUpper(new WatermarkReading("100", now), "int", 30));
        Assert.Null(MonitorCheckService.SettleUpper(new WatermarkReading(null, now), "datetime2", 30));
    }

    [Fact]
    public async Task Proximity_rule_reports_only_inside_rows_with_nearest_layer_details()
    {
        var layer = TestLayer();
        var columns = CoordinateColumns();
        var page = new PageResult(["Id", "Place", "Lat", "Lon"],
            [[11, "A", 0d, 0.1d], [12, "A", 0d, 0.5d]], false, null);
        var metadata = new FakeMetadata(columns, page);
        var reports = new FakeReports();
        var monitor = ProximityMonitor(layer.Id);
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), reports, new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, monitor, CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal("Anchor", result.Rows[0][^3]);
        Assert.Equal("Imported sites", result.Rows[0][^2]);
        Assert.Equal(MonitorProximity.HaversineMeters(0, 0, 0, 0.1), result.Rows[0][^1]);
        Assert.Equal(new[] { "Id", "Place", "Lat", "Lon", "Nearest point name", "Coordinate layer", "Distance (m)" }, result.Columns);
        Assert.Equal(result.Columns, reports.Columns);
        Assert.Equal("12", result.NewWatermark);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Missing_selected_layer_fails_the_check_instead_of_disabling_proximity()
    {
        var metadata = new FakeMetadata(CoordinateColumns(), new PageResult(["Id", "Place", "Lat", "Lon"], [], false, null));
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), new FakeReports(), new FakeLayers());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(Settings, ProximityMonitor(Guid.NewGuid()), CancellationToken.None));

        Assert.True(error.Message.Contains("missing or unreadable", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, metadata.PageCalls);
    }

    [Fact]
    public async Task Proximity_scan_pages_past_ten_thousand_nonmatching_candidates_before_the_match()
    {
        var layer = TestLayer();
        var columns = CoordinateColumns();
        var metadata = new FakeMetadata(columns, request =>
        {
            var rows = request.Offset < 10_000
                ? Enumerable.Range(0, MonitorCheckService.PageSize)
                    .Select(i => new object?[] { (int)request.Offset + i + 1, "A", 0d, 1d }).ToList()
                : new List<object?[]> { new object?[] { 10_001, "A", 0d, 0d } };
            return new PageResult(request.Columns, rows, request.Offset < 10_000, null);
        });
        var service = new MonitorCheckService(metadata, new FakeWatermark("10001"), new FakeReports(), new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, ProximityMonitor(layer.Id, "0"), CancellationToken.None);

        Assert.Equal(11, metadata.PageCalls);
        Assert.Single(result.Rows);
        Assert.Equal(10_001, result.Rows[0][0]);
        Assert.Equal("10001", result.NewWatermark);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Proximity_row_cap_advances_only_through_the_last_examined_matching_candidate()
    {
        var layer = TestLayer();
        var metadata = new FakeMetadata(CoordinateColumns(), request =>
        {
            var rows = Enumerable.Range(0, MonitorCheckService.PageSize)
                .Select(i => new object?[] { (int)request.Offset + i + 1, "A", 0d, 0d }).ToList();
            return new PageResult(request.Columns, rows, true, null);
        });
        var service = new MonitorCheckService(metadata, new FakeWatermark("20000"), new FakeReports(), new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, ProximityMonitor(layer.Id, "0"), CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(MonitorCheckService.MaxRows, result.Rows.Count);
        Assert.Equal(11, metadata.PageCalls); // probe that the next candidate is past the capped watermark
        Assert.Equal("10000", result.NewWatermark);
    }

    [Fact]
    public async Task Out_of_range_database_coordinate_is_not_a_match_and_does_not_fail_the_check()
    {
        var layer = TestLayer();
        var reports = new FakeReports();
        var metadata = new FakeMetadata(CoordinateColumns(),
            new PageResult(["Id", "Place", "Lat", "Lon"], [[11, "A", 91d, 0d]], false, null));
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), reports, new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, ProximityMonitor(layer.Id), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Equal(0, reports.Calls);
    }

    [Fact]
    public async Task Show_all_puts_a_record_outside_the_radius_on_the_map_but_not_in_the_report()
    {
        var layer = TestLayer();
        var reports = new FakeReports();
        var metadata = new FakeMetadata(CoordinateColumns(),
            new PageResult(["Id", "Place", "Lat", "Lon"], [[11, "Far", 50d, 0d]], false, null));
        var monitor = ProximityMonitor(layer.Id) with { LiveMapEnabled = true, LiveMapShowAllMatches = true };
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), reports, new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, monitor, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Equal(0, reports.Calls);
        Assert.Single(result.MapPoints!);
        Assert.Equal(50d, result.MapPoints![0].Latitude);
    }

    [Fact]
    public async Task Without_show_all_a_record_outside_the_radius_is_not_on_the_map()
    {
        var layer = TestLayer();
        var metadata = new FakeMetadata(CoordinateColumns(),
            new PageResult(["Id", "Place", "Lat", "Lon"], [[11, "Far", 50d, 0d]], false, null));
        var monitor = ProximityMonitor(layer.Id) with { LiveMapEnabled = true };
        var service = new MonitorCheckService(metadata, new FakeWatermark("12"), new FakeReports(), new FakeLayers(layer));

        var result = await service.CheckAsync(Settings, monitor, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Empty(result.MapPoints!);
    }

    [Theory]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, true)]
    [InlineData(30, 29, false)]
    [InlineData(30, 31, true)]
    [InlineData(0, 100_000, false)]
    public void Live_results_expire_after_their_display_time_and_never_when_until_cleared(int minutes, int ageMinutes, bool expired)
    {
        var seen = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expired, LiveResultLifetime.IsExpired(seen, seen.AddMinutes(ageMinutes), minutes));
    }

    [Fact]
    public void Unknown_live_display_time_falls_back_to_the_default_and_new_monitors_default_to_off_and_inside_radius()
    {
        Assert.Equal(LiveResultLifetime.DefaultMinutes, LiveResultLifetime.Normalize(7));
        Assert.Equal(LiveResultLifetime.UntilCleared, LiveResultLifetime.Normalize(0));

        var monitor = Monitor();
        Assert.Equal(LiveResultLifetime.DefaultMinutes, monitor.LiveMapMinutes);
        Assert.False(monitor.LiveMapShowAllMatches);
    }

    private static MonitorDefinition LiveMonitor(bool enabled = true) =>
        Monitor("10", DateTime.UtcNow.AddHours(-1)) with
        {
            LatitudeColumn = "Lat",
            LongitudeColumn = "Lon",
            LiveMapEnabled = enabled
        };

    private static MonitorCheckService LiveService(FakeMetadata metadata, FakeReports? reports = null) =>
        new(metadata, new FakeWatermark("12"), reports ?? new FakeReports(), new FakeLayers());

    private static FakeMetadata LiveRows(params object?[][] rows) =>
        new(CoordinateColumns(), new PageResult(["Id", "Place", "Lat", "Lon"], rows, false, null));

    [Fact]
    public async Task Live_map_gives_a_point_for_a_matching_record_with_valid_coordinates()
    {
        var service = LiveService(LiveRows([11, "A", 35.7d, 51.4d]));

        var result = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);

        Assert.Single(result.Rows);
        var point = Assert.Single(result.MapPoints!);
        Assert.Equal("11", point.Key);
        Assert.Equal("A", point.Name);
        Assert.Equal(35.7d, point.Latitude);
        Assert.Equal(51.4d, point.Longitude);
        Assert.Equal(0, result.MapSkippedRows);
    }

    [Theory]
    [InlineData(null, 51.4d)]
    [InlineData(35.7d, null)]
    [InlineData("abc", 51.4d)]
    [InlineData(95.0d, 51.4d)]
    [InlineData(35.7d, 181.0d)]
    public async Task Unusable_coordinates_keep_the_record_in_the_report_but_not_on_the_map(object? latitude, object? longitude)
    {
        var reports = new FakeReports();
        var service = LiveService(LiveRows([11, "A", latitude, longitude]), reports);

        var result = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Empty(result.MapPoints!);
        Assert.Equal(1, result.MapSkippedRows);
        Assert.Equal(1, reports.Calls);
    }

    [Fact]
    public async Task A_malformed_record_does_not_stop_the_next_valid_record()
    {
        var service = LiveService(LiveRows([11, "A", null, 51.4d], [12, "B", 35.7d, 51.4d]));

        var result = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        var point = Assert.Single(result.MapPoints!);
        Assert.Equal("12", point.Key);
        Assert.Equal(1, result.MapSkippedRows);
    }

    [Fact]
    public async Task A_null_in_an_unrelated_column_does_not_stop_the_map_point()
    {
        var service = LiveService(LiveRows([11, null, 35.7d, 51.4d]));

        var result = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);

        var point = Assert.Single(result.MapPoints!);
        Assert.Equal("11", point.Name); // no text value in the row: the key is the name
    }

    [Fact]
    public async Task Live_map_disabled_reports_normally_and_adds_no_points()
    {
        var reports = new FakeReports();
        var service = LiveService(LiveRows([11, "A", 35.7d, 51.4d]), reports);

        var result = await service.CheckAsync(Settings, LiveMonitor(enabled: false), CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Null(result.MapPoints);
        Assert.Equal(1, reports.Calls);
    }

    [Fact]
    public async Task Checking_the_same_record_again_gives_the_same_key_so_the_map_point_is_updated_not_duplicated()
    {
        var service = LiveService(LiveRows([11, "A", 35.7d, 51.4d]));

        var first = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);
        var second = await service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None);

        Assert.Equal(first.MapPoints!.Single().Key, second.MapPoints!.Single().Key);
    }

    [Fact]
    public void Coordinate_columns_alone_are_not_a_proximity_rule()
    {
        var monitor = LiveMonitor();

        Assert.False(monitor.HasProximityRule);
        Assert.True(monitor.HasCoordinateColumns);
    }

    [Fact]
    public async Task A_missing_coordinate_column_in_the_table_still_fails_the_check()
    {
        var metadata = new FakeMetadata(
            [new ColumnInfo("Id", "int", 1), new ColumnInfo("Place", "nvarchar", 0)],
            new PageResult(["Id", "Place"], [[11, "A"]], false, null));
        var service = LiveService(metadata);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(Settings, LiveMonitor(), CancellationToken.None));
    }

    [Fact]
    public async Task Live_map_flag_round_trips_and_defaults_to_disabled_for_old_files()
    {
        var path = Path.Combine(Path.GetTempPath(), "fdbx-" + Guid.NewGuid().ToString("N"), "monitors.json");
        try
        {
            var store = new JsonMonitorStore(path);
            await store.SaveAsync([LiveMonitor(enabled: true)]);
            Assert.True(Assert.Single(await store.LoadAsync()).LiveMapEnabled);

            // An older file has no such field at all: it must load with the map option off.
            var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            node["Monitors"]![0]!.AsObject().Remove("LiveMapEnabled");
            await File.WriteAllTextAsync(path, node.ToJsonString());

            Assert.False(Assert.Single(await store.LoadAsync()).LiveMapEnabled);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static IReadOnlyList<ColumnInfo> CoordinateColumns() =>
    [
        new ColumnInfo("Id", "int", 1),
        new ColumnInfo("Place", "nvarchar", 0),
        new ColumnInfo("Lat", "float", 0),
        new ColumnInfo("Lon", "float", 0)
    ];

    private static MapLayer TestLayer()
    {
        var id = Guid.NewGuid();
        return new MapLayer(id, "Imported sites", "sites.xlsx",
            [new MapPoint(Guid.NewGuid(), "Anchor", 0, 0, id)], visibility: false, radiusMeters: 0);
    }

    private static MonitorDefinition ProximityMonitor(Guid layerId, string? last = "10") =>
        Monitor(last, DateTime.UtcNow.AddHours(-1)) with
        {
            CoordinateLayerId = layerId,
            LatitudeColumn = "Lat",
            LongitudeColumn = "Lon",
            DistanceMeters = 20_000
        };

    private sealed class FakeMetadata(
        IReadOnlyList<ColumnInfo> columns, PageResult? page = null, Func<PageRequest, PageResult>? pageFactory = null) : IDatabaseMetadataService
    {
        public FakeMetadata(IReadOnlyList<ColumnInfo> columns, Func<PageRequest, PageResult> pageFactory)
            : this(columns, null, pageFactory) { }

        public int PageCalls { get; private set; }
        public PageRequest? LastRequest { get; private set; }

        public Task TestConnectionAsync(ConnectionSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(ConnectionSettings settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DatabaseInfo>>([]);
        public Task<IReadOnlyList<TableInfo>> GetTablesAsync(ConnectionSettings settings, string database, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TableInfo>>([]);
        public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionSettings settings, string database, string schema, string table, CancellationToken ct) =>
            Task.FromResult(columns);
        public Task<PageResult> GetPageAsync(ConnectionSettings settings, PageRequest request, CancellationToken ct)
        {
            PageCalls++;
            LastRequest = request;
            return Task.FromResult(pageFactory?.Invoke(request) ?? page!);
        }
    }

    private sealed class FakeLayers(params MapLayer[] layers) : ICoordinateLayerStore
    {
        public Task<IReadOnlyList<MapLayer>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MapLayer>>(layers);

        public Task SaveAsync(MapLayer layer, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> RemoveAsync(Guid layerId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeWatermark(string? max, DateTime? serverNow = null) : IWatermarkReader
    {
        public Task<WatermarkReading> ReadAsync(ConnectionSettings settings, string database, string schema, string table, string column, CancellationToken ct) =>
            Task.FromResult(new WatermarkReading(max, serverNow));
    }

    private sealed class FakeReports : IReportWriter
    {
        public int Calls { get; private set; }
        public int RowCount { get; private set; }
        public IReadOnlyList<string> Columns { get; private set; } = [];

        public Task<string> WriteAsync(string monitorName, DateTime checkedAtUtc, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, string folder, CancellationToken ct)
        {
            Calls++;
            RowCount = rows.Count;
            Columns = columns;
            return Task.FromResult("fake.xlsx");
        }
    }
}
