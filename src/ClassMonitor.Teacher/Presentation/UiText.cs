using System.Text.RegularExpressions;

namespace ClassMonitor.Teacher.Presentation;

/// <summary>Original product strings. Unknown legacy strings remain readable English and are tracked as localization gaps.</summary>
public static partial class UiText
{
    private static readonly Dictionary<string, (string Arabic, string Hebrew)> Catalog = new(StringComparer.Ordinal)
    {
        ["ClassMonitor — Teacher Console"] = ("ClassMonitor — لوحة المعلم", "ClassMonitor — מסוף המורה"),
        ["Classroom monitoring console"] = ("لوحة إدارة الصف", "מסוף ניהול כיתה"),
        ["Start lesson"] = ("بدء الدرس", "התחלת שיעור"),
        ["End lesson"] = ("إنهاء الدرس", "סיום שיעור"),
        ["Question"] = ("سؤال", "שאלה"),
        ["Enroll device"] = ("تسجيل جهاز", "רישום מכשיר"),
        ["Add computer"] = ("إضافة جهاز", "הוספת מחשב"),
        ["Remove computer"] = ("إزالة الجهاز", "הסרת מחשב"),
        ["Computer name or IP address"] = ("اسم الجهاز أو عنوان IP", "שם המחשב או כתובת IP"),
        ["Computer password"] = ("كلمة مرور الجهاز", "סיסמת המחשב"),
        ["Classroom"] = ("الصف", "כיתה"),
        ["Use Add computer to add a student computer."] = ("استخدم «إضافة جهاز» لإضافة جهاز طالب.", "השתמשו ב\"הוספת מחשב\" כדי להוסיף מחשב תלמיד."),
        ["Enter the computer, the classroom and its password."] = ("أدخل الجهاز والصف وكلمة المرور (8 أحرف على الأقل).", "הזינו מחשב, כיתה וסיסמה (לפחות 8 תווים)."),
        ["Adding computer…"] = ("جارٍ إضافة الجهاز…", "מוסיף מחשב…"),
        ["The password is not correct."] = ("كلمة المرور غير صحيحة.", "הסיסמה שגויה."),
        ["Too many wrong passwords. Try again in 15 minutes."] = ("محاولات خاطئة كثيرة. حاول بعد 15 دقيقة.", "יותר מדי ניסיונות שגויים. נסו שוב בעוד 15 דקות."),
        ["The computer refused this request."] = ("رفض الجهاز هذا الطلب.", "המחשב דחה את הבקשה."),
        ["The computer could not be reached. Check the address, and that ClassMonitor is running there and allowed through its firewall."] = ("تعذر الوصول إلى الجهاز. تأكد من العنوان، ومن أن ClassMonitor يعمل عليه ومسموح به في جدار الحماية.", "לא ניתן להגיע למחשב. בדקו את הכתובת, ש-ClassMonitor פועל בו ושהוא מותר בחומת האש."),
        ["This computer could not be verified, so it was not added."] = ("تعذر التحقق من هذا الجهاز، فلم تتم إضافته.", "לא ניתן לאמת את המחשב, ולכן הוא לא נוסף."),
        ["Settings center"] = ("مركز الإعدادات", "מרכז ההגדרות"),
        ["Preferences apply to this console. Enrollment and permissions are controlled separately."] = ("تطبّق التفضيلات على هذه اللوحة. التسجيل والصلاحيات تُدار بشكل منفصل.", "ההעדפות חלות על מסוף זה. רישום מכשירים והרשאות מנוהלים בנפרד."),
        ["Search settings"] = ("البحث في الإعدادات", "חיפוש בהגדרות"),
        ["Settings categories"] = ("فئات الإعدادات", "קטגוריות ההגדרות"),
        ["Device presence filter"] = ("تصفية الأجهزة حسب الاتصال", "סינון לפי נוכחות המכשירים"),
        ["Search devices"] = ("البحث عن أجهزة", "חיפוש מכשירים"),
        ["No matching settings"] = ("لا توجد إعدادات مطابقة", "לא נמצאו הגדרות מתאימות"),
        ["General"] = ("عام", "כללי"), ["Network"] = ("الشبكة", "רשת"),
        ["Display"] = ("العرض", "תצוגה"), ["Files"] = ("الملفات", "קבצים"),
        ["Recording"] = ("التسجيل", "הקלטה"), ["Layout"] = ("التخطيط", "פריסה"),
        ["Focus"] = ("التركيز", "מיקוד"), ["Security"] = ("الأمان", "אבטחה"),
        ["Updates"] = ("التحديثات", "עדכונים"), ["Diagnostics"] = ("التشخيص", "אבחון"),
        ["Language"] = ("اللغة", "שפה"), ["Theme"] = ("المظهر", "ערכת נושא"),
        ["Enable notifications"] = ("تفعيل الإشعارات", "הפעלת התראות"),
        ["Agent TCP port"] = ("منفذ TCP للطالب", "יציאת TCP של סוכן התלמיד"),
        ["Enable LAN discovery"] = ("تفعيل اكتشاف أجهزة الشبكة", "הפעלת גילוי מכשירים ברשת המקומית"),
        ["Discovery UDP port"] = ("منفذ UDP للاكتشاف", "יציאת UDP לגילוי"),
        ["Local IPv4 interface (empty = all)"] = ("عنوان IPv4 المحلي (فارغ = الكل)", "כתובת IPv4 מקומית (ריק = הכול)"),
        ["Connection timeout (seconds)"] = ("مهلة الاتصال (ثوانٍ)", "זמן קצוב לחיבור (שניות)"),
        ["Authentication timeout (seconds)"] = ("مهلة المصادقة (ثوانٍ)", "זמן קצוב לאימות (שניות)"),
        ["Health timeout (seconds)"] = ("مهلة حالة الجهاز (ثوانٍ)", "זמן קצוב לבדיקת תקינות (שניות)"),
        ["Thumbnail width"] = ("عرض الصورة المصغرة", "רוחב תמונה ממוזערת"),
        ["Thumbnail height"] = ("ارتفاع الصورة المصغرة", "גובה תמונה ממוזערת"),
        ["Maximum frame width"] = ("أقصى عرض للصورة", "רוחב מרבי למסגרת"),
        ["JPEG quality"] = ("جودة JPEG", "איכות JPEG"),
        ["Frames per second"] = ("الصور في الثانية", "מסגרות לשנייה"),
        ["Bandwidth budget (Mbps)"] = ("حد استهلاك الشبكة (Mbps)", "תקציב רוחב פס (Mbps)"),
        ["Adapt quality to bandwidth"] = ("تكييف الجودة مع سرعة الشبكة", "התאמת האיכות לרוחב הפס"),
        ["Received lesson directory"] = ("مجلد ملفات الدرس المستلمة", "תיקיית קובצי שיעור שהתקבלו"),
        ["Collected lesson directory"] = ("مجلد جمع ملفات الدرس", "תיקיית איסוף קובצי השיעור"),
        ["Maximum transfer (MiB)"] = ("أقصى حجم للنقل (MiB)", "גודל העברה מרבי (MiB)"),
        ["Allow authorized recording"] = ("السماح بالتسجيل المصرح به", "אפשר הקלטה מורשית"),
        ["Recording directory"] = ("مجلد التسجيل", "תיקיית הקלטות"),
        ["Retention (days)"] = ("مدة الاحتفاظ (أيام)", "משך שמירה (ימים)"),
        ["Recording storage limit (MiB)"] = ("حد مساحة التسجيل (MiB)", "מגבלת אחסון הקלטות (MiB)"),
        ["Recording still requires a classroom grant and an explicit recording action."] = ("يبقى التسجيل مشروطًا بصلاحية الصف وإجراء تسجيل صريح.", "הקלטה עדיין מחייבת הרשאת כיתה ופעולת הקלטה מפורשת."),
        ["Remember window size"] = ("حفظ حجم النافذة", "שמירת גודל החלון"),
        ["Window width"] = ("عرض النافذة", "רוחב החלון"), ["Window height"] = ("ارتفاع النافذة", "גובה החלון"),
        ["Classroom sidebar width"] = ("عرض قائمة الصفوف", "רוחב סרגל הכיתות"),
        ["Device ordering"] = ("ترتيب الأجهزة", "סדר המכשירים"),
        ["Confirm before focus mode"] = ("التأكيد قبل وضع التركيز", "אישור לפני מצב מיקוד"),
        ["Maximum focus duration (minutes)"] = ("أقصى مدة للتركيز (دقائق)", "משך מיקוד מרבי (דקות)"),
        ["Restrictions always restore when the authorized session ends."] = ("تُزال القيود دائمًا عند انتهاء الجلسة المصرح بها.", "ההגבלות תמיד משוחזרות בסיום ההפעלה המורשית."),
        ["Save and apply"] = ("حفظ وتطبيق", "שמירה והחלה"), ["Cancel"] = ("إلغاء", "ביטול"),
        ["Import preferences"] = ("استيراد التفضيلات", "ייבוא העדפות"),
        ["Export preferences"] = ("تصدير التفضيلات", "ייצוא העדפות"),
        ["Reset preferences"] = ("إعادة التفضيلات الافتراضية", "איפוס העדפות"),
        ["Browse"] = ("استعراض", "עיון"),
        ["Preferences were not saved"] = ("لم تُحفظ التفضيلات", "ההעדפות לא נשמרו"),
        ["Import failed"] = ("تعذّر الاستيراد", "הייבוא נכשל"), ["Export failed"] = ("تعذّر التصدير", "הייצוא נכשל"),
        ["All devices"] = ("كل الأجهزة", "כל המכשירים"),
        ["Online"] = ("متصل", "מקוון"), ["Offline"] = ("غير متصل", "לא מקוון"),
        ["Pending enrollment"] = ("بانتظار التسجيل", "ממתין לרישום"),
        ["Unhealthy"] = ("يحتاج فحصًا", "דורש בדיקה"),
        ["Name"] = ("الاسم", "שם"), ["Presence"] = ("حالة الاتصال", "נוכחות"),
        ["System"] = ("النظام", "מערכת"), ["Light"] = ("فاتح", "בהיר"), ["Dark"] = ("داكن", "כהה"),
        ["Live view"] = ("عرض مباشر", "תצוגה חיה"), ["Message"] = ("رسالة", "הודעה"),
        ["Lock"] = ("قفل", "נעילה"), ["Unlock"] = ("فتح القفل", "ביטול נעילה"),
        ["Power"] = ("الطاقة", "הפעלה וכיבוי"), ["Shut down"] = ("إيقاف التشغيل", "כיבוי"),
        ["Restart"] = ("إعادة التشغيل", "הפעלה מחדש"), ["Log off"] = ("تسجيل الخروج", "התנתקות"),
        ["Run"] = ("تشغيل", "הפעלה"), ["Run program…"] = ("تشغيل برنامج…", "הפעלת תוכנית…"),
        ["Open website…"] = ("فتح موقع…", "פתיחת אתר…"),
        ["Restrict"] = ("القيود", "הגבלות"), ["Block apps…"] = ("حظر تطبيقات…", "חסימת יישומים…"),
        ["Allow only these apps…"] = ("السماح لهذه التطبيقات فقط…", "אפשר רק יישומים אלה…"),
        ["Unblock apps"] = ("إلغاء حظر التطبيقات", "ביטול חסימת יישומים"),
        ["Block websites…"] = ("حظر مواقع…", "חסימת אתרים…"),
        ["Unblock websites"] = ("إلغاء حظر المواقع", "ביטול חסימת אתרים"),
        ["Block internet (web)"] = ("حظر الإنترنت (الويب)", "חסימת האינטרנט (גלישה)"),
        ["Allow internet"] = ("السماح بالإنترنت", "אפשר אינטרנט"),
        ["Send file to students…"] = ("إرسال ملف للطلاب…", "שליחת קובץ לתלמידים…"),
        ["Collect file from students…"] = ("جمع ملف من الطلاب…", "איסוף קובץ מהתלמידים…"),
        ["Broadcast my screen"] = ("بث شاشتي", "שיתוף המסך שלי"),
        ["Show selected student"] = ("عرض الطالب المحدد", "הצגת התלמיד הנבחר"),
        ["Monitoring"] = ("المتابعة", "מעקב"), ["Snapshots: Off"] = ("اللقطات: متوقفة", "צילומים: כבוי"),
        ["Snapshots: every 1 min"] = ("لقطة كل دقيقة", "צילום בכל דקה"),
        ["Snapshots: every 5 min"] = ("لقطة كل 5 دقائق", "צילום בכל 5 דקות"),
        ["Snapshots: every 15 min"] = ("لقطة كل 15 دقيقة", "צילום בכל 15 דקות"),
        ["Alert keywords…"] = ("كلمات التنبيه…", "מילות התראה…"),
        ["Show alerts window"] = ("إظهار التنبيهات", "הצגת חלון התראות"),
        ["Usage report…"] = ("تقرير الاستخدام…", "דוח שימוש…"),
        ["Connect manually"] = ("اتصال يدوي", "חיבור ידני"), ["Select all"] = ("تحديد الكل", "בחירת הכול"),
        ["Clear"] = ("مسح التحديد", "ניקוי הבחירה"), ["Settings"] = ("الإعدادات", "הגדרות"),
        ["Small"] = ("صغير", "קטן"), ["Medium"] = ("متوسط", "בינוני"), ["Large"] = ("كبير", "גדול"),
        ["Performance"] = ("الأداء", "ביצועים"), ["Balanced"] = ("متوازن", "מאוזן"), ["Quality"] = ("الجودة", "איכות"),
        ["Thumbnail size:"] = ("حجم الصور:", "גודל תמונות:"),
        ["New from selection"] = ("صف جديد من التحديد", "קבוצה חדשה מהבחירה"),
        ["Delete group"] = ("حذف المجموعة", "מחיקת קבוצה"),
        ["Classrooms"] = ("الصفوف", "כיתות"), ["Groups"] = ("المجموعات", "קבוצות"),
        ["Take control"] = ("طلب التحكم", "בקשת שליטה"), ["Release control"] = ("إنهاء التحكم", "שחרור שליטה"),
        ["All monitors"] = ("كل الشاشات", "כל המסכים"),
        ["Start lesson"] = ("بدء الدرس", "התחלת שיעור"), ["End lesson"] = ("إنهاء الدرس", "סיום שיעור"),
        ["Enroll device"] = ("تسجيل جهاز", "רישום מכשיר"),
        ["Refresh:"] = ("التحديث:", "רענון:"), ["Size:"] = ("الحجم:", "גודל:"),
        ["Copy diagnostics / نسخ التشخيص"] = ("نسخ التشخيص", "העתקת אבחון")
    };

    public static bool IsRightToLeft(string language) => language is "ar" or "he";
    public static IReadOnlyCollection<string> Keys => Catalog.Keys;
    public static string Normalize(string english) => DecorativePrefix().Replace(english, "").Trim();
    public static bool IsKnown(string english) => Catalog.ContainsKey(english) || Catalog.ContainsKey(DecorativePrefix().Replace(english, "").Trim());
    public static string Text(string english, string language)
    {
        if (language == "en") return english;
        if (language is not ("ar" or "he")) throw new ArgumentOutOfRangeException(nameof(language));
        string key = DecorativePrefix().Replace(english, "").Trim();
        return Catalog.TryGetValue(english, out var exact) ? (language == "ar" ? exact.Arabic : exact.Hebrew)
            : Catalog.TryGetValue(key, out var value) ? (language == "ar" ? value.Arabic : value.Hebrew) : english;
    }
    [GeneratedRegex(@"^[^\p{L}\p{Nd}]+")]
    private static partial Regex DecorativePrefix();
}
