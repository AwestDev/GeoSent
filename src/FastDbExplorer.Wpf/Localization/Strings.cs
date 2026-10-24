using FastDbExplorer.Domain;

namespace FastDbExplorer.Wpf.Localization;

/// <summary>All user-facing text. Moving to .resx later only requires changing this file.</summary>
public static class Strings
{
    public const string Tagline = "کاوش داده، نقشه و پایش؛ در یک برنامه";
    public const string Brand1 = "فقط‌خواندنی: هیچ دستور تغییری روی پایگاه‌داده اجرا نمی‌شود.";
    public const string Brand2 = "طراحی‌شده برای جدول‌های بسیار بزرگ، با کمترین فشار روی سرور.";
    public const string Brand3 = "رمز عبور هرگز روی دیسک ذخیره نمی‌شود.";

    public const string ConnectTitle = "اتصال به سرور";
    public const string ConnectSubtitle = "اطلاعات SQL Server را وارد کنید";
    public const string Recent = "اتصال‌های اخیر";
    public const string RemoveRecent = "حذف از فهرست";
    public const string Server = "آدرس سرور";
    public const string Authentication = "روش احراز هویت";
    public const string WindowsAuth = "Windows";
    public const string SqlAuth = "SQL Server";
    public const string UserName = "نام کاربری";
    public const string Password = "رمز عبور";
    public const string TrustCert = "اعتماد به گواهی سرور (سرورهای داخلی با گواهی خودامضا)";
    public const string Connect = "اتصال";
    public const string Test = "تست اتصال";
    public const string Cancel = "لغو";

    public const string Connecting = "در حال برقراری ارتباط…";
    public const string TestOk = "اتصال با موفقیت برقرار شد.";
    public const string Cancelled = "عملیات لغو شد.";

    public const string ConnectedReadOnly = "متصل · فقط‌خواندنی";
    public const string Disconnect = "قطع اتصال";
    public const string Databases = "پایگاه‌داده‌ها";
    public const string Tables = "جدول‌ها";
    public const string SearchTables = "جستجوی جدول…";
    public const string Reload = "بارگذاری مجدد";
    public const string Loading = "در حال بارگذاری…";
    public const string NoTables = "جدولی یافت نشد";
    public const string EmptyTitle = "یک جدول را انتخاب کنید";
    public const string EmptySubtitle = "پایگاه‌داده و جدول موردنظر را از پنل کناری انتخاب کنید.";
    public const string Database = "پایگاه‌داده";
    public const string Schema = "اسکیما";
    public const string ApproxRows = "تعداد ردیف (تقریبی)";
    public const string NextStepNote = "مشاهده و فیلتر داده‌ها در مرحله‌ی بعد (MVP-02) اضافه می‌شود.";

    public const string Columns = "ستون‌ها";
    public const string Filters = "فیلترها";
    public const string SelectAll = "همه";
    public const string SelectNone = "هیچ";
    public const string AddFilter = "+ افزودن فیلتر";
    public const string MatchAll = "همه شرط‌ها (و)";
    public const string MatchAny = "هر شرط (یا)";
    public const string Run = "اجرا (F5)";
    public const string Previous = "قبلی";
    public const string Next = "بعدی";
    public const string PageSizeLabel = "تعداد در هر صفحه";
    public const string OrderBy = "مرتب‌سازی";
    public const string OrderByKey = "کلید اصلی:";
    public const string PickOrderColumn = "ستون مرتب‌سازی (جدول کلید اصلی ندارد)";
    public const string ResultHint = "ستون‌ها و فیلترها را تنظیم کنید و «اجرا» را بزنید.";
    public const string NoResults = "نتیجه‌ای یافت نشد.";
    public const string ExportExcel = "خروجی Excel";
    public const string ExportCompleted = "خروجی Excel ذخیره شد: {0} ({1:N0} ردیف)";
    public const string SendAllToMap = "ارسال همه‌ی نتایج به نقشه";
    public const string ErrSendingToMap = "ارسال به نقشه انجام نشد: ";
    public static string ResultCounting(long total) => $"در حال شمارش… {total:N0} تاکنون";
    public static string ResultFound(long total) => $"{total:N0} رکورد یافت شد";
    public static string MapSendingProgress(int scanned) => $"بررسی برای نقشه: {scanned:N0} رکورد";
    public static string MapSentAll(int points, int skipped) =>
        $"{points:N0} نقطه روی نقشه ارسال شد؛ {skipped:N0} رکورد بدون مختصات معتبر کنار گذاشته شد.";
    public static string MapSentLimited(int points) =>
        $"فقط {points:N0} نقطه (سقف ارسال) روی نقشه آمد. برای نتیجه‌ی کامل، جستجو را دقیق‌تر کنید.";
    public const string ExportFailed = "ساخت فایل Excel انجام نشد: ";
    public const string ShowSelectedOnMap = "نمایش ردیف‌های انتخاب‌شده روی نقشه";
    public const string MapTooltipColumns = "جزئیات نشانگر نقشه";
    public const string MapTooltipColumnsHint = "حداکثر ۸ ستون برای نمایش هنگام رفتن موس روی نقطه";
    public const string LatitudeColumn = "عرض جغرافیایی";
    public const string LongitudeColumn = "طول جغرافیایی";
    public const string ErrSelectRows = "ابتدا یک یا چند ردیف را انتخاب کنید.";
    public const string ErrPickCoordinateColumns = "ستون‌های عرض و طول جغرافیایی را انتخاب کنید.";
    public const string ErrNoValidCoordinates = "در ردیف‌های انتخاب‌شده مختصات معتبر پیدا نشد.";
    public const string ToggleSettings = "نمایش یا پنهان‌کردن تنظیمات";
    public const string ValueTip = "مقدار";
    public const string Value2Tip = "تا مقدار";
    public const string PersianDateTimePickerHint = "تاریخ شمسی و ساعت/دقیقه را انتخاب کنید؛ ثانیه خودکار ۰۰ است و مقدار جست‌وجو میلادی ذخیره می‌شود.";
    public const string PersianDateTimePickerTooltip = "انتخاب تاریخ شمسی و ساعت/دقیقه";
    public const string PersianDateTimePickerTime = "زمان";
    public const string PersianDateTimePickerHour = "ساعت";
    public const string PersianDateTimePickerMinute = "دقیقه";
    public const string PersianDateTimePickerInvalidTime = "ساعت ۰۰ تا ۲۳ و دقیقه ۰۰ تا ۵۹ وارد کنید.";
    public const string PersianDateTimePickerToday = "امروز";
    public const string PersianDateTimePickerClear = "پاک کردن";
    public const string PersianDateTimePickerCancel = "لغو";
    public const string PersianDateTimePickerApply = "تأیید";
    public const string PersianDateTimePickerMonth = "انتخاب ماه";
    public const string PersianDateTimePickerYear = "انتخاب سال";
    public const string PersianDateTimePickerPreviousMonth = "ماه قبل";
    public const string PersianDateTimePickerNextMonth = "ماه بعد";
    public const string PersianDateTimePickerIncreaseHour = "افزایش ساعت";
    public const string PersianDateTimePickerDecreaseHour = "کاهش ساعت";
    public const string PersianDateTimePickerIncreaseMinute = "افزایش دقیقه";
    public const string PersianDateTimePickerDecreaseMinute = "کاهش دقیقه";
    public static IReadOnlyList<string> PersianMonthNames { get; } =
    [
        "", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
        "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"
    ];
    public static IReadOnlyList<string> PersianWeekdayNames { get; } =
        ["شنبه", "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه"];
    public static IReadOnlyList<string> PersianWeekdayLabels { get; } = ["ش", "ی", "د", "س", "چ", "پ", "ج"];
    public const string ErrNoColumns = "حداقل یک ستون را انتخاب کنید.";
    public const string ErrIncompleteFilter = "یکی از فیلترها ناقص است؛ ستون و مقدار را کامل کنید یا آن را حذف کنید.";
    public const string ErrPickOrder = "برای صفحه‌بندی یک ستون مرتب‌سازی انتخاب کنید.";
    public const string ErrInvalidQuery = "درخواست نامعتبر: ";

    public const string Map = "نقشه";
    public const string OpenMap = "باز کردن نقشه";
    public const string OpenMapFile = "باز کردن فایل نقشه";
    public const string Back = "بازگشت";
    public const string FitToMap = "نمایش کل نقشه";
    public const string Layers = "لایه‌ها";
    public const string ToggleLayers = "نمایش یا پنهان‌کردن لایه‌ها";
    public const string NoVectorLayers = "این نقشه تصویری است و لایه‌ی جداگانه ندارد.";
    public const string MapEmptyTitle = "یک فایل نقشه باز کنید";
    public const string MapEmptyBody = "فرمت‌های پشتیبانی‌شده: MBTiles (وکتور و تصویری)، GMDB (کش تصویری GMap.NET)، PBF (نیازمند تبدیل). فایل را به این صفحه بکشید یا از دکمه‌ی زیر انتخاب کنید.";
    public const string MapQueryPending = "ردیف‌های انتخاب‌شده آماده‌اند؛ برای نمایش نقاط، یک فایل نقشه باز کنید.";
    public const string Zoom = "بزرگ‌نمایی";
    public const string LoadingMap = "در حال خواندن فایل نقشه…";
    public const string AssetsMissingTitle = "موتور نقشه نصب نشده است";
    public const string AssetsMissingBody = "فایل maplibre-gl.js پیدا نشد. فایل setup-map-assets.cmd را یک‌بار اجرا کنید (نیاز به اینترنت دارد)، سپس برنامه را دوباره باز کنید.";
    public const string WebViewMissingTitle = "WebView2 در این سیستم در دسترس نیست";
    public const string WebViewMissingBody = "برای نمایش نقشه، «Microsoft Edge WebView2 Runtime» لازم است. آن را از سایت مایکروسافت نصب کنید و دوباره تلاش کنید. جزئیات: ";
    public const string OsmTitle = "این فایل PBF داده‌ی خام OpenStreetMap است";
    public const string OsmBody = "این فایل تایل نقشه نیست و مستقیم قابل نمایش نیست؛ ابتدا باید به MBTiles تبدیل شود (با ابزارهایی مثل tilemaker یا Planetiler). فایل‌های بسیار حجیم مثل asia-latest را پیشنهاد نمی‌کنیم؛ همان منطقه‌ی موردنیاز (مثلاً فقط ایران) را از Geofabrik بگیرید. تبدیل‌گر داخل برنامه در مرحله‌ی بعد اضافه می‌شود. ";
    public const string SchemaTitle = "ساختار این فایل هنوز شناخته‌شده نیست";
    public const string SchemaBody = "این فایل SQLite است اما جدول «tiles» ندارد. اگر GMDB است، این فهرست را (یا یک نمونه‌ی کوچک از فایل را) برای توسعه‌دهنده بفرستید. ";
    public const string UnknownTitle = "این فرمت پشتیبانی نمی‌شود";
    public const string UnknownBody = "جزئیات: ";
    public const string CorruptTitle = "خواندن فایل نقشه ممکن نشد";

    public static string RowsRange(long from, long to) => $"ردیف {from:N0} تا {to:N0}";

    public static string TablesCount(int shown, int total) =>
        shown == total ? $"{total:N0} جدول" : $"{shown:N0} از {total:N0} جدول";

    public static string Describe(DatabaseAccessException ex) => ex.Kind switch
    {
        DatabaseErrorKind.LoginFailed => "ورود ناموفق بود. نام کاربری، رمز عبور یا سطح دسترسی را بررسی کنید.",
        DatabaseErrorKind.ServerUnreachable =>
            "سرور در دسترس نیست. آدرس، پورت، فایروال و فعال‌بودن پروتکل TCP/IP را بررسی کنید. "
            + "(اتصال به localhost از طریق Shared Memory انجام می‌شود، اما 127.0.0.1 حتماً به TCP/IP نیاز دارد.)"
            + Environment.NewLine + ex.Message,
        DatabaseErrorKind.Timeout => "مهلت اجرا به پایان رسید (۳۰ ثانیه). فیلترها را دقیق‌تر کنید.",
        DatabaseErrorKind.PagingNotSupported =>
            "این نسخه یا سطح سازگاری SQL Server صفحه‌بندی را پشتیبانی نمی‌کند. جزئیات تشخیص و حداقل نیازمندی‌ها:" + Environment.NewLine
            + ex.Message + Environment.NewLine
            + "برنامه هیچ تغییری در پایگاه‌داده ایجاد نکرده است؛ برای ادامه، نسخه یا سطح سازگاری را با مدیر سرور بررسی کنید.",
        DatabaseErrorKind.PagingDetectionFailed =>
            "برنامه نتوانست نسخه سرور و سطح سازگاری این پایگاه‌داده را با پرس‌وجوی فقط‌خواندنی تشخیص دهد؛ برای جلوگیری از اجرای روش ناسازگار، جستجو متوقف شد." + Environment.NewLine
            + ex.Message + Environment.NewLine
            + "هیچ تغییری در پایگاه‌داده ایجاد نشده است.",
        DatabaseErrorKind.PagingFallbackFailed =>
            "روش جایگزین صفحه‌بندی (فقط‌خواندنی) هم اجرا نشد. نسخه و سطح سازگاری تشخیص‌داده‌شده و خطای دقیق سرور:" + Environment.NewLine
            + ex.Message + Environment.NewLine
            + "هیچ تغییری در پایگاه‌داده ایجاد نشده است.",
        _ => "خطای پایگاه‌داده: " + ex.Message
    };
}
