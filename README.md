# داشبورد فروش لحظه‌ای (Realtime Sales BI)

*A live sales dashboard: events stream into a rolling in-memory window, and snapshots push to every connected browser over SignalR — no polling, no page refresh. ASP.NET Core 10 · SignalR · minimal APIs · xUnit. See below for the Persian write-up.*

یک داشبورد فروش زنده: رویدادها به یک پنجره‌ی درحال‌غلتیدن در حافظه جریان
می‌یابند، و عکس‌های لحظه‌ای (snapshot) روی **SignalR** به هر مرورگر متصل
push می‌شوند — بدون polling، بدون رفرش صفحه.

**ASP.NET Core 10 · SignalR · minimal APIs · xUnit.** بدون پایگاه‌داده،
بدون سرویس بیرونی؛ `dotnet run` و کار می‌کند.

---

## چه‌کاری انجام می‌دهد

```
 producer ──▶ SlidingWindowAggregator ──▶ broadcast tick ──▶ SignalR ──▶ browsers
 (12/s)        (پنجره‌ی ۵ دقیقه‌ای،         (هر ۱ ثانیه)
                bucketهای ۳۰ ثانیه‌ای)
```

این دو حلقه **عمداً از هم جدا شده‌اند**. broadcast به‌ازای هر رویداد در
throughput بالا کلاینت‌ها را غرق می‌کرد؛ broadcast روی یک تیک ثابت هزینه‌ی
کلاینت را ثابت نگه می‌دارد صرف‌نظر از سرعت ورود رویدادها. با ۱۲ رویداد در
ثانیه، این یعنی ۱۲ ingest و ۱ push در ثانیه.

رویدادها یا از تولیدکننده‌ی مصنوعی داخلی به aggregator می‌رسند یا از طریق
`POST /api/events` از یک سیستم سفارش واقعی.

---

## اندازه‌گیری‌شده روی یک اجرای زنده

بعد از حدود ۴ دقیقه با نرخ ۱۲ رویداد در ثانیه:

| | |
|---|---|
| درآمد در پنجره | £۱۶۳٬۶۶۲٫۱۹ |
| تعداد سفارش در پنجره | ۱٬۶۹۸ |
| میانگین سفارش | £۹۶٫۳۹ |
| فاصله‌ی push | ۹۹۸ میلی‌ثانیه (هدف ۱۰۰۰ میلی‌ثانیه) |
| Timeline | ۱۰ bucket × ۳۰ ثانیه |

تفکیک منطقه‌ای: North £۴۷٫۶k / South £۳۸٫۴k / East £۳۲٫۳k / West £۲۳٫۷k /
Central £۱۶٫۹k — همسو با وزن‌های پیکربندی‌شده‌ی مولد، که یک تست عقلانیت
ارزان برای درست‌بودن گروه‌بندی aggregation است.

---

## شروع سریع

```bash
dotnet run --project src/RealtimeBi.Api
```

بعد `http://localhost:5240` را باز کن. داشبورد هنگام بارگذاری وصل می‌شود و
در کمتر از یک ثانیه شروع به آپدیت‌شدن می‌کند.

```bash
dotnet test        # ۲۸ آزمون
```

---

## API

| متد | مسیر | هدف |
|---|---|---|
| `GET` | `/health` | اندازه‌ی پنجره، تعداد رویدادهای نگه‌داشته‌شده، کلاینت‌های متصل |
| `GET` | `/api/snapshot` | عکس لحظه‌ای فعلی روی HTTP ساده |
| `POST` | `/api/events` | تزریق یک رویداد فروش واقعی به پنجره |
| — | `/hub/dashboard` | هاب SignalR — push با `snapshot`، pull با `RequestSnapshot` |

```bash
curl -X POST http://localhost:5240/api/events \
  -H "Content-Type: application/json" \
  -d '{"orderId":"ORD-1","region":"West","channel":"Web",
       "amount":249.99,"occurredAt":"2026-08-03T10:15:00Z"}'
```

تنظیمات زیر کلید `Feed` در `appsettings.json` قرار دارند: `EventsPerSecond`،
`BroadcastIntervalMs`، `WindowMinutes`، `BucketSeconds`،
`GenerateSyntheticEvents`. مقادیر نامعتبر همان **موقع راه‌اندازی** خطا
می‌دهند، نه در اولین درخواست.

---

## تصمیم‌های طراحی که ارزش توضیح دارند

**قفل خواننده/نویسنده، نه یک `lock` ساده.** ingest می‌نویسد؛ snapshot
می‌خواند. چند thread هم‌زمان snapshot می‌گیرند (حلقه‌ی broadcast، درخواست‌های
HTTP، فراخوانی‌های hub) درحالی‌که فقط تولیدکننده می‌نویسد، پس
`ReaderWriterLockSlim` اجازه می‌دهد خواننده‌ها موازی اجرا شوند و فقط
نویسنده را سریالایز می‌کند.

**Snapshotها رکوردهای immutable‌اند.** یک snapshot که به یک کلاینت push
شده هرگز با رویداد بعدی تغییر نمی‌کند — یک آزمون تضمین می‌کند snapshotای که
قبل از یک ingest گرفته شده، همچنان مجموع قدیمی را نشان می‌دهد.

**Eviction هنگام نوشتن اتفاق می‌افتد، نه روی تایمر.** نیازی به یک sweeper
پس‌زمینه‌ی همیشه‌روشن نیست، و بافر بین دو sweep نمی‌تواند رشد کند. یک سقف
سخت `maxEvents` هم هست، چون رویدادهای با تاریخ آینده هرگز صرفاً با گذر زمان
منقضی نمی‌شدند.

**Timeline از قبل هر bucket را pre-seed می‌کند.** یک ۳۰ ثانیه‌ی ساکت به‌شکل
یک میله‌ی صفر رندر می‌شود، نه یک شکاف که چارت مجبور باشد بینش را
درون‌یابی کند.

**اتصال بلافاصله push می‌کند.** کلاینتی که تازه وصل شده، در غیر این‌صورت تا
تیک بعدی به صفحه‌ی خالی خیره می‌ماند. `OnConnectedAsync` بلافاصله snapshot
فعلی را برای همان تماس‌گیرنده می‌فرستد.

**Broadcast وقتی کسی تماشا نمی‌کند رد می‌شود.** اگر هیچ کلاینتی وصل نباشد،
حلقه اصلاً یک projection نمی‌سازد.

**اعتبارسنجی روی لبه.** `SalesEvent.Validate()` قبل از این‌که یک رویداد
بتواند وارد پنجره شود اجرا می‌شود، پس یک payload بدشکل نمی‌تواند یک aggregate
درحال‌اجرا را خراب کند. `POST /api/events` خطاهای فیلد به فیلد را برمی‌گرداند.

---

## آزمون‌ها — ۲۸ مورد

| بخش | پوشش می‌دهد |
|---|---|
| Aggregation | مجموع‌ها، تفکیک‌ها، مرتب‌سازی، گردکردن به سنت |
| پنجره | انقضا، مرز، eviction، ورود خارج از ترتیب |
| Timeline | تعداد bucket، پرکردن صفر، ترتیب، تطابق مجموع‌ها |
| هم‌زمانی | ۴۰۰۰ ingest موازی چیزی گم نمی‌کند؛ snapshotها هرگز پاره نمی‌شوند |
| اعتبارسنجی | مبالغ نادرست، null، bucket بزرگ‌تر از پنجره |
| HTTP | سلامت، snapshot، تزریق رویداد، خطاهای سطح فیلد |
| SignalR | snapshot هنگام اتصال، broadcast زنده، pull درخواستی، شمارش اتصال |

رفتار پنجره در برابر یک `TimeProvider` تزریق‌شده تست می‌شود، پس یک آزمون
انقضای ۶ دقیقه‌ای بلافاصله اجرا می‌شود، نه با sleep‌کردن.

آزمون‌های SignalR از یک `HubConnection` واقعی روی `WebApplicationFactory`
استفاده می‌کنند — آن‌ها انتقال واقعی را تمرین می‌دهند، نه یک mock.

---

## ساختار

```
realtime-bi/
├── src/RealtimeBi.Api/
│   ├── Domain/          # SalesEvent, DashboardSnapshot (رکوردهای immutable)
│   ├── Services/        # SlidingWindowAggregator, generator, FeedWorker, options
│   ├── Hubs/            # DashboardHub
│   ├── wwwroot/         # داشبورد (چارت SVG دستی، بدون کتابخانه‌ی چارت)
│   └── Program.cs
└── tests/RealtimeBi.Tests/
    ├── AggregatorTests.cs
    └── ApiTests.cs
```

## مجوز

MIT
