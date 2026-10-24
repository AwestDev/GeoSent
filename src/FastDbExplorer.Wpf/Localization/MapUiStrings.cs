namespace FastDbExplorer.Wpf.Localization;

/// <summary>Texts of the redesigned map panel, the marker (icon/colour) picker and the detached map window.</summary>
public static class MapUiStrings
{
    public const string AddExcelLayer = "افزودن لایه Excel";
    public const string ShowAll = "نمایش همه";
    public const string HideAll = "پنهان‌کردن همه";
    public const string NoMapLayers = "این نقشه لایهٔ جداگانه‌ای برای کنترل ندارد.";
    public const string EmptyExcelTitle = "هنوز لایه‌ای اضافه نشده";
    public const string EmptyExcelBody = "یک فایل Excel شامل نام و مختصات (عرض و طول جغرافیایی) اضافه کنید تا نقاط روی نقشه نمایش داده شوند.";
    public const string LayerSettings = "تنظیمات لایه";
    public const string ToggleVisibility = "نمایش یا پنهان‌کردن لایه";
    public const string MarkerLabel = "نماد روی نقشه";
    public const string MarkerPickerHint = "انتخاب نماد";
    public const string ColorLabel = "رنگ";
    public const string DefaultMarker = "پیش‌فرض (نقطه ساده)";

    public const string Screenshot = "گرفتن تصویر از نقشه (PNG)";
    public const string ScreenshotTitle = "ذخیرهٔ تصویر نقشه";
    public const string ScreenshotFailed = "گرفتن تصویر از نقشه انجام نشد.";

    public const string PopOut = "باز کردن در پنجره جدا";
    public const string DockBack = "بازگرداندن به برنامه";
    public const string PoppedOutTitle = "نقشه در پنجره جدا باز است";
    public const string PoppedOutBody = "می‌توانید پنجرهٔ نقشه را روی مانیتور دیگری قرار دهید. برای برگرداندن، دکمهٔ زیر را بزنید.";

    public static string TabExcel(int count) => $"نقاط Excel ({count})";

    public static string TabMap(int count) => $"لایه‌های نقشه ({count})";

    public static string MapWindowTitle(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? "نقشه — FastDbExplorer" : $"{fileName} — نقشه";

    public static string CategoryName(string key) => key switch
    {
        "general" => "عمومی",
        "health" => "درمانی",
        "education" => "آموزشی",
        "religion" => "مذهبی",
        "transport" => "حمل‌ونقل",
        "government" => "دولتی",
        "security" => "امنیتی و نظامی",
        "energy" => "انرژی و صنعت",
        "nature" => "طبیعت و زیرساخت",
        "commerce" => "تجاری و خدمات",
        "culture" => "فرهنگی و تفریحی",
        _ => key
    };

    public static string IconName(string key) => key switch
    {
        "pin" => "نشانگر مکان",
        "dot" => "دایره",
        "target" => "هدف",
        "flag" => "پرچم",
        "star" => "ستاره",
        "heart" => "علاقه‌مندی",
        "home" => "خانه",
        "person" => "فرد",

        "hospital" => "بیمارستان",
        "medical" => "درمانگاه",
        "pharmacy" => "داروخانه",
        "lab" => "آزمایشگاه",

        "school" => "مدرسه",
        "university" => "دانشگاه",
        "library" => "کتابخانه",

        "mosque" => "مسجد",
        "church" => "کلیسا",
        "temple" => "معبد",
        "cemetery" => "قبرستان",

        "metro" => "مترو",
        "bus" => "اتوبوس و ایستگاه",
        "plane" => "فرودگاه",
        "ship" => "بندر",
        "car" => "خودرو",
        "fuel" => "پمپ‌بنزین",
        "bridge" => "پل",

        "parliament" => "مجلس و نهاد دولتی",
        "court" => "دادگاه",
        "police" => "پلیس",
        "fire" => "آتش‌نشانی",
        "post" => "پست",

        "shield" => "امنیتی",
        "camera" => "دوربین و نظارت",
        "warning" => "هشدار و منطقهٔ خطر",
        "military" => "پادگان و نظامی",
        "radar" => "رادار",
        "checkpoint" => "ایست‌بازرسی",

        "factory" => "کارخانه",
        "bolt" => "برق",
        "power_plant" => "نیروگاه",
        "nuclear" => "هسته‌ای",
        "solar" => "خورشیدی",

        "drop" => "آب",
        "tower" => "دکل مخابرات",
        "tree" => "جنگل و پارک",
        "mountain" => "کوهستان",

        "shop" => "فروشگاه",
        "bank" => "بانک",
        "building" => "ساختمان اداری",
        "hotel" => "هتل",
        "restaurant" => "رستوران",
        "cafe" => "کافه",

        "museum" => "موزه",
        "stadium" => "ورزشگاه",
        "ticket" => "سالن و بلیت",
        "monument" => "بنای یادبود",

        _ => key
    };
}
