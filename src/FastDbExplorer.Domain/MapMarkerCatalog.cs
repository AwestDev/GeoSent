namespace FastDbExplorer.Domain;

/// <summary>
/// One map marker glyph: a filled shape on a 24x24 grid (SVG path data, even-odd fill). It is drawn in white on a
/// coloured disc, both in the layer list (WPF) and on the map (canvas Path2D), so the two always look the same.
/// All glyphs are drawn in-house for this project (no third-party icon licence).
/// </summary>
public sealed record MapMarkerIcon(string Key, string PathData);

/// <summary>A folder of marker glyphs in the icon picker (for example "health" or "transport").</summary>
public sealed record MapMarkerCategory(string Key, IReadOnlyList<MapMarkerIcon> Icons);

/// <summary>The built-in marker icons and colours a coordinate layer can use. Pure data, no UI types.</summary>
/// <remarks>
/// Icon keys are stored in the layer files, so an existing key must never be renamed or reused for another shape.
/// New icons are appended to a category. The category key is only used for grouping, not stored anywhere.
/// </remarks>
public static class MapMarkerCatalog
{
    /// <summary>The category whose first entry is the default dot marker (no icon).</summary>
    public const string DefaultCategory = "general";

    public static IReadOnlyList<string> Palette { get; } =
    [
        "#4F46E5", "#2563EB", "#0D9488", "#16A34A", "#D97706",
        "#EA580C", "#DC2626", "#DB2777", "#9333EA", "#475569",
        "#EAB308", "#84CC16", "#0891B2", "#0EA5E9", "#92400E", "#111827"
    ];

    public static IReadOnlyList<MapMarkerCategory> Categories { get; } =
    [
        new(DefaultCategory,
        [
            new("pin", "M12 2 C8.1 2 5 5.1 5 9 c0 5.2 7 13 7 13 s7 -7.8 7 -13 c0 -3.9 -3.1 -7 -7 -7 z M12 6.5 a2.5 2.5 0 1 0 0 5 a2.5 2.5 0 1 0 0 -5 z"),
            new("dot", "M12 6 a6 6 0 1 0 0 12 a6 6 0 1 0 0 -12 z"),
            new("target", "M12 3 a9 9 0 1 0 0 18 a9 9 0 1 0 0 -18 z M12 6 a6 6 0 1 1 0 12 a6 6 0 1 1 0 -12 z M12 9 a3 3 0 1 0 0 6 a3 3 0 1 0 0 -6 z"),
            new("flag", "M5 3 h2 v18 H5 z M7 4 h12 l-3 4.5 l3 4.5 H7 z"),
            new("star", "M12 2 l3.1 6.3 l6.9 1 l-5 4.9 l1.2 6.8 l-6.2 -3.3 l-6.2 3.3 l1.2 -6.8 l-5 -4.9 l6.9 -1 z"),
            new("heart", "M12 21 C12 21 4 15.6 4 10 a4.5 4.5 0 0 1 8 -2.8 A4.5 4.5 0 0 1 20 10 c0 5.6 -8 11 -8 11 z"),
            new("home", "M12 3 L2 12 h3 v8 h5 v-5 h4 v5 h5 v-8 h3 z"),
            new("person", "M12 4 a4 4 0 1 0 0 8 a4 4 0 1 0 0 -8 z M4 21 c0 -4.4 3.6 -8 8 -8 s8 3.6 8 8 z")
        ]),
        new("health",
        [
            new("hospital", "M9 3 h6 v6 h6 v6 h-6 v6 H9 v-6 H3 V9 h6 z"),
            new("medical", "M12 2 a10 10 0 1 0 0 20 a10 10 0 1 0 0 -20 z M10.5 6 h3 v4.5 h4.5 v3 h-4.5 V18 h-3 v-4.5 H6 v-3 h4.5 z"),
            new("pharmacy", "M3.5 12 a4.5 4.5 0 0 1 4.5 -4.5 h4 v9 h-4 a4.5 4.5 0 0 1 -4.5 -4.5 z M16 7.5 h0.5 a4.5 4.5 0 0 1 0 9 H16 z"),
            new("lab", "M9 3 h6 v1 h-1 v5 l5 9 a1.5 1.5 0 0 1 -1.3 2.2 H6.3 A1.5 1.5 0 0 1 5 18 l5 -9 V4 H9 z")
        ]),
        new("education",
        [
            new("school", "M12 4 L1 9.5 l11 5.5 l9 -4.5 V16 h2 V9.5 L12 4 z M5.5 13.5 v3.8 c1 0 3 3.2 6.5 3.2 s6.5 -2.2 6.5 -3.2 v-3.8 L12 16.5 l-6.5 -3 z"),
            new("university", "M12 2 L22 7 V9 H2 V7 z M4 11 h2 v7 H4 z M9 11 h2 v7 H9 z M13 11 h2 v7 h-2 z M18 11 h2 v7 h-2 z M2 19 h20 v3 H2 z"),
            new("library", "M3 4 h4 v16 H3 z M8 4 h4 v16 H8 z M14 6 l3.9 -1 l4.2 14.6 l-3.9 1 z M3 21 h18 v1.5 H3 z")
        ]),
        new("religion",
        [
            new("mosque", "M12 3 c-3.3 2.6 -5 4.8 -5 7 v1 h10 v-1 c0 -2.2 -1.7 -4.4 -5 -7 z M6 12 h12 v9 H6 z M10.5 21 v-4 a1.5 1.5 0 0 1 3 0 v4 z M2.5 9 h2 v12 h-2 z M19.5 9 h2 v12 h-2 z M3.5 5 l1.2 4 h-2.4 z M20.5 5 l1.2 4 h-2.4 z"),
            new("church", "M11 1 h2 v2 h2 v2 h-2 v2 h-2 V5 H9 V3 h2 z M12 8 L19 13 V22 H5 V13 z M10 16 h4 v6 h-4 z M8 14 h2 v2 H8 z M14 14 h2 v2 h-2 z"),
            new("temple", "M12 3 a6 6 0 0 1 6 6 H6 a6 6 0 0 1 6 -6 z M4 9 h16 v2 H4 z M6 11 h2 v10 H6 z M11 11 h2 v10 h-2 z M16 11 h2 v10 h-2 z M3 21 h18 v2 H3 z"),
            new("cemetery", "M7 21 V10 a5 5 0 0 1 10 0 V21 z M11 9 h2 v2 h2 v2 h-2 v3 h-2 v-3 H9 v-2 h2 z M4 21 h16 v2 H4 z")
        ]),
        new("transport",
        [
            new("metro", "M6 2 h12 a2 2 0 0 1 2 2 v13 a3 3 0 0 1 -3 3 l1.5 2 v0.5 h-2 l-1.5 -2 h-5 l-1.5 2 h-2 v-0.5 L7 20 a3 3 0 0 1 -3 -3 V4 a2 2 0 0 1 2 -2 z M7 5 h10 v6 H7 z M9 13.5 a1.5 1.5 0 1 0 0 3 a1.5 1.5 0 1 0 0 -3 z M15 13.5 a1.5 1.5 0 1 0 0 3 a1.5 1.5 0 1 0 0 -3 z"),
            new("bus", "M5 3 h14 a1 1 0 0 1 1 1 v13 a1 1 0 0 1 -1 1 v2 h-3 v-2 H8 v2 H5 v-2 a1 1 0 0 1 -1 -1 V4 a1 1 0 0 1 1 -1 z M6.5 6 h11 v5 h-11 z M7 13.5 a1 1 0 1 0 0 2 a1 1 0 1 0 0 -2 z M17 13.5 a1 1 0 1 0 0 2 a1 1 0 1 0 0 -2 z"),
            new("plane", "M12 2 L13.5 9 L22 14 V16 L13.5 13.5 V18.5 L16 20.5 V22 L12 21 L8 22 V20.5 L10.5 18.5 V13.5 L2 16 V14 L10.5 9 z"),
            new("ship", "M2 16 l3 5 h14 l3 -5 z M7 10 h10 v4 H7 z M10 5 h4 v4 h-4 z"),
            new("car", "M3 13 l2 -5 a2 2 0 0 1 1.9 -1.4 h10.2 a2 2 0 0 1 1.9 1.4 l2 5 v5 h-2 v2 h-3 v-2 H8 v2 H5 v-2 H3 z M7 9 l1.2 -1.9 h2.3 V9 z M12 9 V7.1 h3 l1.3 1.9 z"),
            new("fuel", "M4 3 h10 v18 H4 z M6.5 6 h5 v4.5 h-5 z M14 9 h2 l3 2 v7 a1.5 1.5 0 0 1 -3 0 v-6 h-2 z"),
            new("bridge", "M2 17 h20 v2 H2 z M4 17 C4 9 8 7 12 7 C16 7 20 9 20 17 h-2.5 C17.5 11.5 15.5 10 12 10 C8.5 10 6.5 11.5 6.5 17 z")
            ,new("airport", "M9 6.5 h6 v3 H9 z M7.5 2.5 h9 v3 h-9 z M11 9.5 h2 v12 h-2 z M7.5 21.5 h9 v2 h-9 z"),
            new("port", "M10 2 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 z M11 5 h2 v11 h4 v-1.5 h2 v3 c0 2.5 -3.5 4 -7 4 s-7 -1.5 -7 -4 v-3 h2 v1.5 h4 z")
        ]),
        new("government",
        [
            new("parliament", "M12 2 L21 8 H3 z M4 10 h16 v2 H4 z M5 13 h2.5 v6 H5 z M10.75 13 h2.5 v6 h-2.5 z M16.5 13 h2.5 v6 h-2.5 z M3 20 h18 v2 H3 z"),
            new("court", "M11 8.8 h2 v9.2 h4 v2 H7 v-2 h4 z M11 3 h2 v4 h-2 z M4 7 h16 v1.8 H4 z M5 9 L2 15 H8 z M19 9 L16 15 H22 z"),
            new("police", "M3 14 a9 9 0 0 1 18 0 z M2 16 h20 v2 H2 z M10.5 2 h3 v3 h-3 z"),
            new("fire", "M12 2 C12 2 18 7 18 13 a6 6 0 0 1 -12 0 c0 -2.2 1 -3.7 2.5 -5 c0.2 2 1.2 3 2.5 3.2 C14 9 12 5.5 12 2 z"),
            new("post", "M3 5 h18 v14 H3 z M5.5 6.5 h13 L12 12 z")
            ,new("embassy", "M11.3 0.5 h1.4 v3.5 h-1.4 z M12.7 0.8 h6 l-1.4 1.7 l1.4 1.7 h-6 z M2 9 L12 3.5 L22 9 V10.5 H2 z M4 12 h2.6 v7 H4 z M10.7 12 h2.6 v7 h-2.6 z M17.4 12 h2.6 v7 h-2.6 z M2 20.5 h20 v2.5 H2 z"),
            new("town_hall", "M12 1.5 L16.5 6 H7.5 z M8 6.5 h8 v14 H8 z M12 9 a2.6 2.6 0 1 0 0 5.2 a2.6 2.6 0 1 0 0 -5.2 z M10.8 15 h2.4 v5.5 h-2.4 z M4 20.5 h16 v2.5 H4 z")
        ]),
        new("security",
        [
            new("shield", "M12 2 l8 3 v6 c0 5 -3.4 9.3 -8 11 c-4.6 -1.7 -8 -6 -8 -11 V5 z"),
            new("camera", "M4 7 h3 l1.5 -2.5 h7 L17 7 h3 a1 1 0 0 1 1 1 v11 a1 1 0 0 1 -1 1 H4 a1 1 0 0 1 -1 -1 V8 a1 1 0 0 1 1 -1 z M12 9.5 a3.8 3.8 0 1 0 0 7.6 a3.8 3.8 0 1 0 0 -7.6 z"),
            new("warning", "M12 2 L1 21 h22 z M11 9 h2 v6 h-2 z M11 17 h2 v2 h-2 z"),
            new("military", "M7 10 a5 5 0 0 1 10 0 z M5 10 h14 v4 H5 z M2 14 h19 v4 H2 z M17 8 h4.5 v1.4 H17 z"),
            new("radar", "M2 13 a10 10 0 0 1 20 0 z M11 13 h2 v7 h3 v2 H8 v-2 h3 z"),
            new("checkpoint", "M2 20 h20 v2 H2 z M3 6 h2 v14 H3 z M19 6 h2 v14 h-2 z M5 9 h14 v2 H5 z")
            ,new("fort", "M2 8 h3 V5 h3 v3 h2 V5 h3 v3 h3 V5 h3 v3 h3 V5 h3 v3 H2 z M2 8 h20 v13 H2 z M10 14 h4 v7 h-4 z")
        ]),
        new("energy",
        [
            new("factory", "M2 21 V10 L8 14 V10 L14 14 V10 L18 12.7 V4 H22 V21 z"),
            new("bolt", "M13 2 L4 14 h6 l-1 8 l9 -12 h-6 z"),
            new("power_plant", "M2 21 L4.8 7.5 C6 5.5 8 5.5 9.2 7.5 L11.5 21 z M12.5 21 L15 8 C16 6 18 6 19 8 L21.5 21 z M1 21 h22 v2 H1 z"),
            new("nuclear", "M7.25 3.77 A9.5 9.5 0 0 1 16.75 3.77 L13.75 8.97 A3.5 3.5 0 0 0 10.25 8.97 z M21.5 12 A9.5 9.5 0 0 1 16.75 20.23 L13.75 15.03 A3.5 3.5 0 0 0 15.5 12 z M7.25 20.23 A9.5 9.5 0 0 1 2.5 12 L8.5 12 A3.5 3.5 0 0 0 10.25 15.03 z M14.2 12 a2.2 2.2 0 1 0 -4.4 0 a2.2 2.2 0 1 0 4.4 0 z"),
            new("solar", "M2 14.5 L13 8 L21 13.5 L10 20.5 z M11 20 h2 v3 h-2 z M7 23 h10 v1 H7 z")
            ,new("wind_turbine", "M11 10.5 h2 v11.5 h-2 z M10.4 10 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0 z M11 9.4 L8.4 1.2 L10.3 0.9 L12.6 9.4 z M13.1 10.4 L21.6 8.3 L21.9 10.1 L13.5 11.6 z M10.6 11 L4.2 15.6 L5.2 17.1 L11.3 12.6 z")
        ]),
        new("nature",
        [
            new("drop", "M12 2.5 C12 2.5 5 10 5 14.5 a7 7 0 0 0 14 0 C19 10 12 2.5 12 2.5 z"),
            new("tower", "M12 7 l8 14 h-3.2 L12 12.5 L7.2 21 H4 z M12 2.5 a2.2 2.2 0 1 0 0 4.4 a2.2 2.2 0 1 0 0 -4.4 z"),
            new("tree", "M12 2 L18 11 H15 L19 17 H14 V22 H10 V17 H5 L9 11 H6 z"),
            new("mountain", "M2 20 L9 7 L13 13 L16 9 L22 20 z")
        ]),
        new("commerce",
        [
            new("shop", "M6 8 h12 l1 13 H5 z M9 8 V7 a3 3 0 0 1 6 0 v1 h-2 V7 a1 1 0 0 0 -2 0 v1 z"),
            new("bank", "M12 2 L2 8 h20 z M4 10 h3 v8 H4 z M10.5 10 h3 v8 h-3 z M17 10 h3 v8 h-3 z M2 20 h20 v2 H2 z"),
            new("building", "M5 3 h10 v18 H5 z M7.5 6 h2 v2 h-2 z M10.5 6 h2 v2 h-2 z M7.5 10 h2 v2 h-2 z M10.5 10 h2 v2 h-2 z M7.5 14 h2 v2 h-2 z M10.5 14 h2 v2 h-2 z M16.5 9 h3 v12 h-3 z"),
            new("hotel", "M2 6 h2 v13 H2 z M4 16 h18 v3 H4 z M4 9 h16 v5 H4 z M5 6.5 h4 v2.5 H5 z"),
            new("restaurant", "M4 2 h1.6 v6 h1.2 V2 h1.6 v6 h1.2 V2 h1.6 v6.5 c0 1.5 -1.2 2.5 -2.5 2.5 V22 h-2 v-11 c-1.3 0 -2.7 -1 -2.7 -2.5 z M16.5 2 c2.5 1 3.5 4 3.5 8 v3 h-3 V22 h-2 V2 z"),
            new("cafe", "M4 8 h13 v6 a6 6 0 0 1 -6 6 h-1 a6 6 0 0 1 -6 -6 z M17 9.5 h1.5 a2.5 2.5 0 0 1 0 5 H17 v-1.6 h1.5 a0.9 0.9 0 0 0 0 -1.8 H17 z M3 21 h16 v2 H3 z")
        ]),
        new("culture",
        [
            new("museum", "M3 3 h18 v18 H3 z M5.5 5.5 v13 h13 v-13 z M7 17 L10.5 12 L13 15 L14.5 13 L17.5 17 z"),
            new("stadium", "M5 6 h14 a6 6 0 0 1 0 12 H5 a6 6 0 0 1 0 -12 z M8.5 9 h7 a3 3 0 0 1 0 6 h-7 a3 3 0 0 1 0 -6 z"),
            new("ticket", "M2 7 a2 2 0 0 0 2 -2 h16 a2 2 0 0 0 2 2 v3 a2 2 0 0 0 0 4 v3 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 v-3 a2 2 0 0 0 0 -4 z M15 6.5 h1.2 v11 H15 z"),
            new("monument", "M10.5 4 L12 1.5 L13.5 4 V15 h-3 z M8 15 h8 v2 H8 z M5 17 h14 v5 H5 z")
        ])
    ];

    /// <summary>Every icon, in category order. Flat list kept for lookups and for the existing callers.</summary>
    public static IReadOnlyList<MapMarkerIcon> Icons { get; } =
        [.. Categories.SelectMany(category => category.Icons)];

    public static MapMarkerIcon? Find(string? key) =>
        key is null ? null : Icons.FirstOrDefault(icon => icon.Key == key);

    /// <summary>"#RRGGBB" only (no names, no alpha), so a stored value can never inject anything into the map page.</summary>
    public static bool IsValidColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>The colour to draw: the chosen one, else a stable palette colour picked from the layer id.</summary>
    public static string EffectiveColor(Guid layerId, string? color)
    {
        if (IsValidColor(color)) return color!.ToUpperInvariant();

        uint hash = 17;
        foreach (var b in layerId.ToByteArray()) hash = unchecked(hash * 31 + b);
        return Palette[(int)(hash % (uint)Palette.Count)];
    }
}
