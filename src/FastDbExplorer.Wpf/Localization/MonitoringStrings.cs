using System.Globalization;
using FastDbExplorer.Domain;

namespace FastDbExplorer.Wpf.Localization;

/// <summary>All user-facing text of the monitoring (مانیتورینگ و هشدار) screens.</summary>
public static class MonitoringStrings
{
    public const string Title = "مانیتورینگ و هشدار";
    public const string Subtitle = "شرط‌ها را تعریف کنید؛ برنامه در بازه‌های زمانی انتخابی، فقط رکوردهای جدید را (به‌صورت فقط‌خواندنی) بررسی می‌کند.";
    public const string OpenMonitoring = "مانیتورینگ";
    public const string NewMonitor = "مانیتور جدید";
    public const string Edit = "ویرایش";
    public const string Delete = "حذف";
    public const string ConfirmDeleteQuestion = "حذف شود؟";
    public const string Yes = "بله";
    public const string No = "خیر";
    public const string CheckNow = "بررسی اکنون";
    public const string Enabled = "فعال";
    public const string NoMonitors = "هنوز مانیتوری تعریف نشده است. با «مانیتور جدید» شروع کنید.";
    public const string Alerts = "هشدارها";
    public const string NoAlerts = "هنوز هشداری ثبت نشده است.";
    public const string OpenReport = "باز کردن گزارش";
    public const string ShowInFolder = "نمایش در پوشه";
    public const string Close = "بستن";
    public const string ShowResults = "نمایش نتایج";
    public const string ResultsTitle = "نتایج بررسی";
    public const string LatitudeColumn = "ستون عرض جغرافیایی";
    public const string LongitudeColumn = "ستون طول جغرافیایی";
    public const string MapDetailsColumns = "جزئیات همراه هر نقطه روی نقشه (حداکثر ۸ ستون)";
    public const string SendSelectedRowsToMap = "ارسال ردیف‌های انتخاب‌شده به نقشه";
    public const string CloseResults = "بستن پیش‌نمایش";
    public const string NullValue = "NULL";
    public const string PreviewTruncated = "فقط ردیف‌های دریافت‌شده در این بررسی نمایش داده می‌شوند؛ ادامه در بررسی بعدی انجام می‌شود.";
    public const string PreviewLimited = "برای کاهش مصرف حافظه، پیش‌نمایش محدود است؛ گزارش Excel شامل همه‌ی ردیف‌های این بررسی است.";
    public const string MaxMapDetailColumnsReached = "حداکثر ۸ ستون جزئیات را می‌توانید برای نقشه انتخاب کنید.";
    public const string NoValidSelectedCoordinates = "در ردیف‌های انتخاب‌شده مختصات معتبر WGS84 پیدا نشد.";
    public const string RunningNote = "بررسی‌ها فقط تا وقتی که برنامه باز و به سرور متصل است انجام می‌شوند. هیچ دستور تغییری روی پایگاه‌داده اجرا نمی‌شود.";
    public const string NeverChecked = "هنوز بررسی نشده";
    public const string FirstCheckNote = "در اولین بررسی فقط نقطه‌ی شروع ثبت می‌شود.";
    public const string BaselineSet = "نقطه‌ی شروع ثبت شد؛ از این پس رکوردهای جدید بررسی می‌شوند.";
    public const string NoNewMatches = "رکورد جدیدِ مطابق شرط نبود.";
    public const string Checking = "در حال بررسی…";
    public const string ErrorPrefix = "⚠ ";

    public const string EditorTitleNew = "مانیتور جدید";
    public const string EditorTitleEdit = "ویرایش مانیتور";
    public const string Name = "نام مانیتور";
    public const string Database = "پایگاه‌داده";
    public const string Table = "جدول";
    public const string Target = "جدول موردنظر";
    public const string WatermarkColumn = "ستون تشخیص رکورد جدید";
    public const string WatermarkHint = "ستونی با مقدار افزایشی که برای پیدا کردن رکوردهای جدید از آخرین بررسی استفاده می‌شود. نام این ستون در هر جدول ممکن است متفاوت باشد؛ از فهرست انتخاب کنید. توصیه: ستون تاریخ ثبت (مثل CreatedAt) دقیق‌تر است؛ کلید عددی افزایشی اگر چند تراکنش هم‌زمان بنویسند ممکن است رکوردی را از قلم بیندازد. ستون باید ایندکس داشته باشد (در جدول بزرگ بدون ایندکس بررسی کند می‌شود).";
    public const string Conditions = "شرط‌ها (مثلاً مکان، مبدا، مقصد)";
    public const string AddCondition = "+ افزودن شرط";
    public const string Interval = "بازه‌ی بررسی (دقیقه)";
    public const string IntervalHint = "مثلاً 60 = هر یک ساعت، 120 = هر دو ساعت";
    public const string OutputFolder = "پوشه‌ی ذخیره‌ی گزارش Excel";
    public const string Browse = "انتخاب پوشه…";
    public const string PlaySound = "پخش صدا هنگام هشدار";
    public const string LiveMapTitle = "نمایش رکوردهای منطبق روی نقشه به‌صورت زنده";
    public const string LiveMapHint = "هر رکورد منطبق که مختصات معتبر داشته باشد، بلافاصله روی نقشه در لایه‌ای با نام این مانیتور نشان داده می‌شود. رکوردهای بدون مختصات معتبر فقط در گزارش می‌آیند و روی نقشه نمی‌آیند.";
    public const string CoordinateColumnsTitle = "ستون‌های مختصات جغرافیایی";
    public const string ErrCoordinateColumns = "دو ستون جداگانه با مقادیر عددی برای عرض و طول جغرافیایی انتخاب کنید.";
    public const string LiveMapDurationTitle = "مدت نمایش نتایج روی نقشه";
    public const string LiveMapDurationHint = "هر نتیجه از زمان آخرین پیدا شدنش روی نقشه می‌ماند. اگر همان رکورد در بررسی بعدی دوباره پیدا شود، زمانش از نو شروع می‌شود. حذف از نقشه، رکورد دیتابیس و فایل اکسل را تغییر نمی‌دهد.";
    public const string LiveMapShowAllTitle = "روی نقشه همه رکوردهای منطبق با شرط‌ها را نشان بده، حتی خارج از شعاع";
    public const string LiveMapShowAllHint = "فقط نقشه تغییر می‌کند: رکوردهای خارج از شعاع در گزارش نمی‌آیند ولی روی نقشه دیده می‌شوند.";

    public static string LiveMapDurationLabel(int minutes) => minutes switch
    {
        LiveResultLifetime.UntilCleared => "تا پاک‌کردن دستی",
        60 => "۱ ساعت",
        _ => $"{minutes} دقیقه"
    };

    /// <summary>Name of the map layer that shows one monitor's matching records.</summary>
    public static string LiveLayerName(string monitorName) => $"پایش زنده: {monitorName}";
    public const string Save = "ذخیره";
    public const string PickFolderTitle = "انتخاب پوشه‌ی ذخیره‌ی گزارش";

    public const string ErrName = "برای مانیتور یک نام وارد کنید.";
    public const string ErrTarget = "پایگاه‌داده و جدول را انتخاب کنید.";
    public const string ErrWatermark = "ستون تشخیص رکورد جدید را انتخاب کنید (ستون عددی یا تاریخ).";
    public const string ErrNoConditions = "حداقل یک شرط تعریف کنید.";

    /// <summary>Shown when the monitor has neither a condition nor a proximity rule (it would alert on every new row).</summary>
    public const string ErrNoConditionsOrProximity =
        "برای مانیتور حداقل یک قاعده لازم است: یا یک شرط (مثلاً مکان) یا هشدار نزدیکی به لایه‌ی مختصات. هر دو با هم هم مجاز است.";

    public const string ErrNoProximityLayer = "حداقل یک لایه‌ی مختصات ذخیره‌شده را برای هشدار نزدیکی انتخاب کنید.";
    public const string ErrEmptyProximityLayer = "یکی از لایه‌های انتخاب‌شده هیچ نقطه‌ای ندارد؛ آن را بردارید یا لایه‌ی دیگری انتخاب کنید.";

    public const string ProximityTitle = "هشدار بر اساس نزدیکی به لایه‌ی مختصات واردشده از Excel";
    public const string ProximityLayers = "لایه‌های مختصات (می‌توانید چند لایه را انتخاب کنید)";
    public const string ProximityDistance = "حداکثر فاصله تا نزدیک‌ترین نقطه (متر)";
    public const string ProximityHint =
        "این قاعده مستقل از شرط‌های بالاست: می‌توانید فقط با همین قاعده ذخیره کنید. اگر شرط هم داشته باشید، رکورد جدید باید هر دو را داشته باشد. مختصات WGS84 بررسی می‌شود و شعاع ترسیم لایه روی نقشه اثری ندارد.";
    public const string ProximityNoLayers =
        "هنوز لایه‌ی مختصاتی ذخیره نشده است؛ از بخش نقشه یک فایل Excel وارد کنید.";

    public static string PointsCount(int count) => $"{count:N0} نقطه";
    public const string ErrInterval = "بازه‌ی بررسی باید عددی بین 1 تا 1440 دقیقه باشد.";
    public const string ErrFolder = "مسیر پوشه‌ی گزارش را کامل وارد کنید (مثلاً D:\\Reports).";
    public const string ErrFolderCreate = "ایجاد یا دسترسی به این پوشه ممکن نیست: ";
    public const string ErrOpenFile = "باز کردن فایل ممکن نشد: ";

    public static string EveryMinutes(int minutes) =>
        minutes >= 60 && minutes % 60 == 0 ? $"هر {minutes / 60} ساعت" : $"هر {minutes} دقیقه";

    public static string LastChecked(DateTime utc) =>
        "آخرین بررسی: " + utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string TimeText(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Matched(int count) => $"{count:N0} رکورد مطابق شرط پیدا شد.";

    public static string PreviewRowCount(int count) => $"{count:N0} ردیف؛ برای انتخاب چند ردیف از Ctrl استفاده کنید.";

    public static string RowsSkippedForMap(int skipped, int sent) =>
        $"{skipped:N0} ردیف به‌دلیل مختصات نامعتبر نادیده گرفته شد؛ {sent:N0} ردیف معتبر به نقشه ارسال شد.";

    public static string RowsSentToMap(int count) => $"{count:N0} ردیف به نقشه ارسال شد.";

    public static string AlertSummary(int count, bool truncated) =>
        truncated
            ? $"{count:N0} رکورد جدید مطابق شرط (فقط اولین دسته در گزارش آمد؛ بقیه در بررسی بعدی)"
            : $"{count:N0} رکورد جدید مطابق شرط";

    public static string ToastTitle(string monitorName) => $"هشدار: {monitorName}";

    public static string ToastErrorTitle(string monitorName) => $"خطا در مانیتور: {monitorName}";

    public static string CheckFailed(string reason) => $"بررسی ناموفق بود: {reason}";
}
