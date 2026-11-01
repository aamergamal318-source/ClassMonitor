# ClassMonitor

## التحديثات وتطوير البرنامج

بدءًا من 1.1.0 يفحص البرنامج أحدث إصدار مستقر على GitHub عند كل تشغيل، ويتيح تنزيله
وتثبيته وإعادة التشغيل من داخل البرنامج. يظهر خيار التحديث أسفل نافذة المعلم وفي قائمة
أيقونة الطالب. تحتاج النسخ الأقدم استبدالًا يدويًا مرة واحدة لتفعيل هذه الميزة.
راجع [دليل التحديث](UPDATE-GUIDE.md) و[خطة التطوير نحو إدارة صف متكاملة](DEVELOPMENT-PLAN.md).

From 1.1.0, both apps check stable GitHub releases at startup and offer verified in-app
installation and restart. Earlier versions require one initial manual upgrade.

## مفتوح المصدر · Open source

المشروع متاح بترخيص [MIT](LICENSE): يمكنك تنزيل المصدر واستخدامه وتعديله وإعادة توزيعه وفق شروط الترخيص.
لتنزيل المصدر من GitHub اختر **Code → Download ZIP**. للبناء والتشغيل اتبع قسم **Build & run** أدناه.
إذا توفّرت نسخة جاهزة، ستجدها في قسم **Releases**؛ نزّل ملف ZIP وفك ضغطه، ثم اتبع [تعليمات النشر](dist/DEPLOY.txt).
البرنامج مخصص لنظام Windows، والمراقبة تتطلب صلاحية إدارة الأجهزة وعلم مستخدميها.

Licensed under [MIT](LICENSE). Download the source with **Code → Download ZIP**, or download a Windows package from **Releases** when available. See [deployment instructions](dist/DEPLOY.txt).

أداة أصلية لإدارة ومراقبة أجهزة صف دراسي / مختبر على شبكة محلية (LAN)، مكتوبة بلغة
C# / .NET 9 لنظام Windows. هي تطبيق مستقل تمامًا، وليست مبنية على أي كود من برامج تجارية.

A from-scratch classroom/lab monitoring and management tool for a trusted LAN, written in
C# / .NET 9 for Windows. Entirely original code — not derived from any commercial product.

---

## الاستخدام المسؤول · Responsible use

هذه الأداة مخصّصة للاستخدام على أجهزة **تملك أنت صلاحية إدارتها** (صف/مختبر/مكتب)، ومع
**علم المستخدمين** بوجودها. وكيل الطالب يُظهر **أيقونة دائمة في شريط المهام** عمدًا حتى
يكون التشغيل شفافًا. لا تستخدمها للمراقبة الخفية لأشخاص دون صلاحية أو موافقة؛ قد يكون ذلك
مخالفًا للقانون.

Use only on machines **you are authorized to administer**, with users **aware** it is
running. The agent shows a **persistent tray icon** on purpose. Do not use it for covert
surveillance of people without authority or consent.

> 🔒 الأمان: كل اتصال الآن **مشفّر بـ TLS** ويُصادَق بـ**كلمة مرور الصف المشتركة** (تحدٍّ/استجابة
> HMAC — كلمة المرور لا تُرسَل). كلمة المرور الافتراضية `classmonitor`؛ غيّرها من `⚙ Settings` في
> اللوحة، واضبط الوكلاء على نفس القيمة في `%ProgramData%\ClassMonitor\agent.json`.
> Security: every connection is now **TLS-encrypted** and authenticated with the **shared class
> password** (HMAC challenge-response; the password never crosses the wire). Default password is
> `classmonitor` — change it via `⚙ Settings`, and set the same value on the agents in
> `%ProgramData%\ClassMonitor\agent.json`.

---

## المعمارية · Architecture

```
┌──────────────────────────┐        UDP beacon (5901)        ┌──────────────────────────┐
│  ClassMonitor.Teacher     │  ◀───────────────────────────  │  ClassMonitor.Agent       │
│  (teacher console, WinForms)                                │  (student machine, tray)  │
│                           │   TCP 5900 (framed messages)    │                           │
│  • discovery + grid       │  ───────────────────────────▶  │  • screen capture (JPEG)  │
│  • live view + control    │     screenshots & commands      │  • message / lock overlay │
│  • broadcast my screen    │  ◀───────────────────────────  │  • remote input inject    │
└──────────────────────────┘                                 │  • show teacher broadcast │
                                                              └──────────────────────────┘
```

- **ClassMonitor.Shared** — message protocol: length-prefixed framing
  (`[4-byte len][1-byte type][payload]`) over TCP, JSON DTOs, and the UDP discovery format.
- **ClassMonitor.Agent** — runs on each student PC. Broadcasts a UDP beacon, listens on TCP
  5900, streams JPEG screenshots, and executes commands (message, lock, remote input, show
  teacher broadcast). Always-visible tray icon.
- **ClassMonitor.Teacher** — the console. Auto-discovers agents, shows a live thumbnail grid,
  opens a full live view with remote control, and broadcasts the teacher's screen to all.

---

## الوظائف · Features (MVP)

- 🖥️ **عرض شاشات الطلاب** — شبكة مصغّرات حيّة (أحجام قابلة للتعديل)؛ نقر مزدوج لعرض شاشة كاملة.
- 🎮 **التحكّم عن بُعد** — توجيه الفأرة ولوحة المفاتيح لجهاز طالب من نافذة العرض الحيّ.
- 💬 **رسائل / قفل الشاشة** — بثّ رسالة للأجهزة المحدّدة أو الكل، وقفل/فتح الشاشات.
- 📡 **بثّ شاشة المعلّم** — عرض شاشة المعلّم على كل أجهزة الطلاب.
- ⏻ **تحكّم بالطاقة** — إطفاء/إعادة تشغيل/تسجيل خروج، مع **حماية ذاتية** تمنع إطفاء جهاز المعلّم.
- ▶️ **تشغيل برنامج / فتح موقع** على الأجهزة المستهدفة.
- 📁 **نقل الملفات** — إرسال ملف للطلاب، أو جمع ملف منهم (يُحفظ باسم الجهاز).
- 💬 **دردشة** ثنائية الاتجاه بين المعلّم وكل طالب.
- 🔴 **تسجيل الشاشة** — تسجيل بثّ أي طالب إلى ملف فيديو AVI (MJPEG) قابل للتشغيل.
- 🚫 **التقييد والحجب** — حجب **التطبيقات** (إنهاء العمليات)، حجب **المواقع** (ملف hosts)، حجب **الإنترنت/الويب** (قاعدة جدار حماية). يتطلب صلاحيات مدير على الوكيل؛ تُرفع القيود تلقائياً عند إيقاف الوكيل.
- 🖥️ **عدّة شاشات** — اختيار شاشة محدّدة للطالب (أو الكل) من نافذة العرض الحيّ.
- 📸 **لقطات مجدولة** — حفظ لقطة لكل جهاز دورياً (كل 1/5/15 دقيقة) إلى مجلّد.
- 🔔 **تنبيهات بالكلمات** — تنبيه عند ظهور كلمة مفتاحية في عنوان النافذة/التطبيق النشط (بلا تسجيل ضغطات مفاتيح)؛ تظهر شارة `⚠ ALERT` وسجلّ تنبيهات.
- 📊 **تقارير استخدام** — تجميع وقت استخدام التطبيقات لكل جهاز، مع تصدير CSV.

التحديد: انقر مربّعاً لتحديده، Ctrl+نقر لتحديد متعدّد؛ الأوامر تُطبّق على المحدّد أو على الكل.
الحالة تظهر كشارات على المربّع: `LIVE` / `LOCKED` / `● REC`.

### الأساس (الأمان والتنظيم والتشغيل) · Foundation

- 🔒 **تشفير TLS + مصادقة بكلمة مرور مشتركة** على كل اتصال.
- 👥 **مجموعات/صفوف محفوظة** — لوحة جانبية: أنشئ مجموعة من التحديد، واختر مجموعة لتحديد أجهزتها.
- ⚙️ **إعدادات** قابلة للحفظ (كلمة المرور، المنفذ، ظاهر/خفي).
- 🚀 **تشغيل تلقائي عند تسجيل الدخول** — من قائمة أيقونة الوكيل، أو:
  `ClassMonitorAgent.exe --install` (يتطلب صلاحيات مدير؛ `--uninstall` للإزالة).
  يعمل في جلسة المستخدم (التقاط الشاشة يعمل)، لا في الجلسة 0 المعزولة.

---

## البناء والتشغيل · Build & run

المتطلبات: .NET 9 SDK على Windows.

```powershell
# build everything
dotnet build ClassMonitor.sln -c Debug

# on each STUDENT machine
dotnet run --project src/ClassMonitor.Agent
#   أو شغّل: src/ClassMonitor.Agent/bin/Debug/net9.0-windows/ClassMonitorAgent.exe

# on the TEACHER machine
dotnet run --project src/ClassMonitor.Teacher
#   أو شغّل: src/ClassMonitor.Teacher/bin/Debug/net9.0-windows/ClassMonitorTeacher.exe
```

الطلاب يظهرون تلقائيًا في لوحة المعلّم خلال ثوانٍ عبر الاكتشاف على الشبكة. تأكّد أن جدار
الحماية (Windows Firewall) يسمح بالمنافذ TCP 5900 و UDP 5901 على الشبكة الخاصة.

Agents appear in the console automatically within a few seconds via discovery. Allow TCP
5900 and UDP 5901 through Windows Firewall on the private network.

### تجربة على جهاز واحد · Try on one machine

يمكنك تشغيل الـ Agent والـ Teacher على نفس الجهاز للتجربة؛ سيكتشف المعلّم المضيف المحلي
ويعرض شاشته. (التحكّم عن بُعد على نفس الجهاز يتداخل مع فأرتك، فجرّبه بين جهازين.)

---

## خارطة الطريق · Roadmap

- [x] تحكّم بالطاقة (إطفاء/إعادة تشغيل/خروج) مع حماية ذاتية.
- [x] تشغيل برنامج / فتح موقع.
- [x] نقل الملفات (إرسال/جمع).
- [x] دردشة ثنائية الاتجاه.
- [x] تسجيل الشاشة إلى ملف AVI.
- [x] TLS + مصادقة بكلمة مرور مشتركة (أمان النقل).
- [x] مجموعات/صفوف أجهزة محفوظة.
- [x] تشغيل تلقائي عند تسجيل الدخول (مهمة مجدولة، تعمل في جلسة المستخدم).
- [x] حجب تطبيقات/مواقع/إنترنت.
- [x] مراقبة متقدّمة: عدّة شاشات، لقطات مجدولة، تنبيهات بالكلمات، تقارير استخدام.
- [ ] خدمة تعمل قبل تسجيل الدخول + كلمة مرور لإلغاء التثبيت.

> ملاحظة: أدوات "الإدارة عن بُعد" الحرّة (تنفيذ أوامر عشوائية، كتابة/حذف ملفات لمسارات حرّة) **لم
> ولن تُنفَّذ** عمداً: فهي سطح تنفيذ أوامر عن بُعد (RCE) خطير وعام الاستخدام الضار.
