<div align="center">

# Magic RDR for Android

<b>Browse, extract, edit and rebuild Red Dead Redemption RPF archives, straight from your phone.</b>

<p dir="rtl"><b>تصفّح أرشيفات Red Dead Redemption بصيغة RPF واستخرجها وعدّلها وأعد بناءها مباشرة من هاتفك.</b></p>

[

![Build](https://github.com/Zlatan-RDR/MagicRDR-Android/actions/workflows/main.yml/badge.svg)

](https://github.com/Zlatan-RDR/MagicRDR-Android/actions/workflows/main.yml)
[

![Android 10+](https://img.shields.io/badge/Android-10%2B-3DDC84?logo=android&logoColor=white)

](#install)
[

![.NET 10 MAUI](https://img.shields.io/badge/.NET_10-MAUI-512BD4?logo=dotnet&logoColor=white)

](#architecture)
[

![Latest release](https://img.shields.io/github/v/release/Zlatan-RDR/MagicRDR-Android?label=release)

](https://github.com/Zlatan-RDR/MagicRDR-Android/releases/latest)
[

![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

](LICENSE)

[English](#english) · [العربية](#العربية)

</div>

---

## English

### About

**Magic RDR for Android** is an Android mobile port of the PC tool **Magic-RDR (v1.3.10) by [ImFoxxyyy](https://github.com/ImFoxxyyy)**. It is a derivative work: the file-format knowledge comes from the original tool, and the application itself is a new implementation in C# (.NET 10 MAUI) that runs entirely on the phone, with no PC needed.

It was built mainly for **translating Red Dead Redemption into Arabic** on a phone, but it works as a general RPF6 browser and editor.

> **Not affiliated with Rockstar Games.** This repository contains no game files. Use it only with copies of the game that you own, and keep a backup of every `.rpf` before saving over it.

### Features

**RPF6 archives**
- Open large `.rpf` archives, browse folders, filter the current folder, and see real names when a names list is loaded (RPF6 stores hashes only).
- Extract a file (decompressed), a whole folder (as a ZIP), or a selection of files.
- Replace files, then rebuild and save the archive. The saved archive is re-opened so you can keep working.
- Hex viewer and text viewer, names import and name discovery, and automatic restore of your last session after the app is closed.

**Multi-select and batch work**
- Multi-select mode with a checkbox on every file, **Select All**, **Select None** and a live `X selected` counter. The selection survives changing folders and filtering.
- **Batch extract:** every selected file goes into one ZIP, named by its real name or its hash (`0x00FEB5CC`), with progress and cancel.
- **Batch replace:** choose several files or one ZIP; files are matched to entries by name or hash name, with or without an extension. You see a summary such as `Matched 24 of 25 selected entries. Replace all?` before anything changes. Ambiguous matches are skipped and listed, never guessed.

**String tables (`.wst` and `.strtbl`)**
- Table view with filter, per-entry editor, Apply entry, Revert, Apply table, and text Export / Import.
- **Nintendo Switch Zstandard frame repair:** packed resources whose compressed data has no end marker are repaired before reading.
- Untouched entries keep their original bytes: editing one line, or importing a file, never rewrites the others.

**Built-in Arabic visual codec ("Arabic Visual Fix")**

The game draws text strictly left to right and cannot shape Arabic. Arabic is therefore stored in *visual* form: letters already replaced by Unicode Presentation Forms (`U+FE70-FEFF`, `U+FB50-FDFF`) and laid out in reverse. Android would reverse such text a second time, so it looks backwards on a phone. The codec fixes this in both directions:
- **Reading and Export:** reverses the visual order, maps Presentation Forms back to normal Arabic letters (including Lam-Alef ligatures), so you read natural, connected Arabic.
- **Apply and Import:** shapes your normal Arabic into Initial / Medial / Final / Isolated forms and lays it out the way the game expects.
- **Tags and tokens stay intact:** markup such as `<red>`, colour codes like `~r~`, placeholders such as `%s` and `{0}`, escape sequences such as `\r` and `\n`, numbers and Latin words are never reversed or scrambled. Brackets and parentheses are mirrored correctly. Every line is handled on its own, and strings without Arabic are not touched.
- A switch in the Table view turns it on or off; it is enabled automatically when stored Arabic is detected.

**Permanent signing key**

Every release is signed with the same permanent key, so a new version installs over the old one and keeps your data. Certificate SHA-256 fingerprint:

```
af8bd63ec56ca5feb91d788f3cd4c8a02799be6b49f7036bba4348d627bf3b8b
```

If a downloaded APK shows a different fingerprint, do not install it. The fingerprint is also written in the notes of every release.

### Install

1. Open the [**latest release**](https://github.com/Zlatan-RDR/MagicRDR-Android/releases/latest) and download `MagicRDR-Android-vX.Y.Z.apk` from **Assets**.
2. Android may ask you to allow installing apps from this source. Allow it for your browser or file manager.
3. Open the APK and install. A newer version installs directly over an older one.
4. Requires **Android 10 (API 29) or newer**.

Android shows an "unknown source" warning for any app installed outside Google Play. That is normal, and the signature above is how you can check the file.

### Quick start

1. Open the app and tap **Open RPF**, then choose an archive.
2. Optional: **Import names** to load a names list so entries show real file names.
3. Open a string table with **View**, then **Table**. Edit, then **Apply table**.
4. Tap **Save RPF** to rebuild the archive and choose where to save it.

### Architecture

```
src/MagicRDR.Core          net10.0 library: RPF6 parsing, table of contents rebuild, deflate / Zstandard,
                           AES table-of-contents crypto, names, string tables, Arabic visual codec (no UI)
src/MagicRDR.App           net10.0-android MAUI app (MVVM). Minimum Android 10 (API 29), target Android 16 (API 36)
tests/MagicRDR.Core.Tests  xUnit tests on synthetic data, run in CI on every build
docs/                      ARCHITECTURE, FEATURE-PARITY, DEVELOPMENT, RELEASING, UPDATING (Arabic)
MagicRDR.sln               solution file
.github/workflows/         CI: unpack sources, test, build, sign, publish a GitHub Release
```

Rules for new features are in `docs/ARCHITECTURE.md`; the signing key and in-place updates are explained in `docs/RELEASING.md`.

### Build and updates (CI only, no PC needed)

This project is built entirely by GitHub Actions. Uploading a new `MagicRDR-project.zip` to the repository root makes the workflow unpack it into the source tree, run the unit tests, build the APK with .NET 10 MAUI, sign it with the permanent key and publish a GitHub Release. See `docs/UPDATING.md`.

### Credits

- **Magic-RDR (PC tool) and the original file-format work:** [ImFoxxyyy](https://github.com/ImFoxxyyy)
- **Android port (Magic RDR for Android):** [Zlatan-RDR](https://github.com/Zlatan-RDR) (Hamza)

### License

[MIT](LICENSE). Copyright for the original tool belongs to ImFoxxyyy; copyright for the Android port belongs to Zlatan-RDR (Hamza).

---

<div dir="rtl" align="right">

## العربية

### نبذة

**Magic RDR for Android** منفذ أندرويد للأداة المكتبية **Magic-RDR (الإصدار 1.3.10) للمطوّر [ImFoxxyyy](https://github.com/ImFoxxyyy)**. هو عمل مشتق: معرفة صيغ الملفات مصدرها الأداة الأصلية، أما التطبيق نفسه فتنفيذ جديد بلغة C# على `.NET 10 MAUI` يعمل بالكامل على الهاتف دون حاجة إلى حاسوب.

صُنع أساساً لـ **تعريب Red Dead Redemption** من الهاتف، ويصلح أيضاً كمتصفّح ومحرّر عام لأرشيفات RPF6.

> **لا علاقة للمشروع بشركة Rockstar Games.** لا يحتوي المستودع على أي ملفات من اللعبة. استخدمه مع نسخة تملكها فقط، واحتفظ بنسخة احتياطية من أي ملف `.rpf` قبل الحفظ فوقه.

### المزايا

**أرشيفات RPF6**
- فتح أرشيفات `.rpf` الكبيرة وتصفّح المجلدات وتصفية المجلد الحالي، وإظهار الأسماء الحقيقية عند تحميل قائمة أسماء (لأن RPF6 يخزّن قيم hash فقط).
- استخراج ملف (بعد فك ضغطه) أو مجلد كامل (ملف ZIP) أو مجموعة ملفات محددة.
- استبدال الملفات ثم إعادة بناء الأرشيف وحفظه، ثم يُعاد فتح الملف المحفوظ لتكمل العمل.
- عارض Hex وعارض نصوص، واستيراد الأسماء واستكشافها، واستعادة جلستك الأخيرة تلقائياً بعد إغلاق التطبيق.

**التحديد المتعدد والعمل الدفعي**
- وضع التحديد المتعدد: مربع اختيار على كل ملف، وأزرار **Select All** و**Select None**، وعدّاد حي `X selected`. يبقى التحديد عند تغيير المجلد أو التصفية.
- **استخراج دفعي:** كل الملفات المحددة في ملف ZIP واحد، تُسمّى باسمها الحقيقي أو بقيمة الـ hash (`0x00FEB5CC`)، مع شريط تقدّم وإمكان الإلغاء.
- **استبدال دفعي:** اختر عدة ملفات أو ملف ZIP واحداً؛ تُطابَق الملفات مع عناصر الأرشيف بالاسم أو باسم الـ hash، بامتداد أو بدونه. يظهر ملخص مثل `Matched 24 of 25 selected entries. Replace all?` قبل أي تغيير. الحالات الملتبسة تُتخطّى وتُعرض، ولا يُخمَّن فيها.

**جداول النصوص (`.wst` و`.strtbl`)**
- عرض جدول مع تصفية ومحرّر لكل مدخل وأزرار Apply entry وRevert وApply table وتصدير واستيراد نصي.
- **إصلاح إطارات Zstandard لنسخة Nintendo Switch:** الموارد المضغوطة التي ينقصها علامة النهاية تُصلَح قبل القراءة.
- المدخلات التي لم تُعدَّل تبقى بايتاتها الأصلية: تعديل سطر واحد أو استيراد ملف لا يعيد كتابة بقية المدخلات.

**مُرمِّز العربية المدمج ("Arabic Visual Fix")**

اللعبة ترسم النص من اليسار إلى اليمين فقط ولا تشكّل الحروف العربية، لذلك تُخزَّن العربية بصيغة *بصرية*: حروف مستبدلة سلفاً بأشكال العرض في Unicode (`U+FE70-FEFF` و`U+FB50-FDFF`) ومرتّبة بترتيب معكوس. ولأن أندرويد يعكس هذا النص مرة ثانية يظهر مقلوباً على الهاتف. يعالج المُرمِّز المشكلة في الاتجاهين:
- **القراءة والتصدير:** يعكس الترتيب البصري ويعيد أشكال العرض إلى الحروف العربية العادية (ومنها فك ligature لام-ألف)، فتقرأ عربية طبيعية متصلة.
- **التطبيق والاستيراد:** يشكّل ما تكتبه بالعربية العادية (أول/وسط/آخر/منفصل) ويرتّبه كما تتوقعه اللعبة.
- **الوسوم والرموز تبقى سليمة:** مثل `<red>` ورموز الألوان `~r~` والعناصر `%s` و`{0}` والرموز `\r` و`\n` والأرقام والكلمات اللاتينية، فلا تُعكس ولا تتبعثر. الأقواس تُقلب بشكل صحيح، وكل سطر يُعالج وحده، والنصوص التي لا عربية فيها لا تُمسّ.
- مفتاح في عرض الجدول لتشغيله وإيقافه، ويُفعَّل تلقائياً عند اكتشاف عربية مخزّنة.

**مفتاح التوقيع الدائم**

كل الإصدارات موقّعة بالمفتاح الدائم نفسه، فيُثبَّت الإصدار الجديد فوق القديم وتبقى بياناتك. بصمة الشهادة بخوارزمية SHA-256:

<div dir="ltr" align="left">

```
af8bd63ec56ca5feb91d788f3cd4c8a02799be6b49f7036bba4348d627bf3b8b
```

</div>

إن ظهرت لملف APK نزّلتَه بصمة مختلفة فلا تثبّته. والبصمة مكتوبة أيضاً في وصف كل Release.

### التثبيت

1. افتح [**أحدث إصدار**](https://github.com/Zlatan-RDR/MagicRDR-Android/releases/latest) ونزّل ملف APK الذي يبدأ اسمه بـ `MagicRDR-Android` من قسم **Assets**.
2. قد يطلب أندرويد السماح بالتثبيت من هذا المصدر؛ اسمح لمتصفحك أو مدير الملفات.
3. افتح ملف APK وثبّته. الإصدار الأحدث يُثبَّت مباشرة فوق الأقدم.
4. يتطلب **أندرويد 10 (API 29) أو أحدث**.

يعرض أندرويد تحذير «مصدر غير معروف» لأي تطبيق يُثبَّت من خارج Google Play، وهذا طبيعي. والبصمة أعلاه هي طريقتك للتحقق من الملف.

### بداية سريعة

1. افتح التطبيق واضغط **Open RPF** ثم اختر أرشيفاً.
2. اختيارياً: **Import names** لتحميل قائمة أسماء فتظهر أسماء الملفات الحقيقية.
3. افتح جدول نصوص بـ **View** ثم **Table**، عدّل ثم اضغط **Apply table**.
4. اضغط **Save RPF** لإعادة بناء الأرشيف واختيار مكان حفظه.

### البنية

- `src/MagicRDR.Core`: مكتبة `net10.0` فيها تحليل RPF6 وإعادة بناء جدول المحتويات وضغط deflate وZstandard وتشفير AES لجدول المحتويات والأسماء وجداول النصوص ومُرمِّز العربية، بلا أي واجهة.
- `src/MagicRDR.App`: تطبيق `net10.0-android` بـ MAUI على نمط MVVM. الحد الأدنى أندرويد 10 (API 29)، والهدف أندرويد 16 (API 36).
- `tests/MagicRDR.Core.Tests`: اختبارات xUnit على بيانات اصطناعية، تعمل في CI مع كل بناء.
- `docs/`: ملفات ARCHITECTURE وFEATURE-PARITY وDEVELOPMENT وRELEASING وUPDATING (بالعربية).
- `MagicRDR.sln`: ملف الحل.
- `.github/workflows/`: الـ CI، وفيه فك المصدر والاختبار والبناء والتوقيع ونشر Release.

قواعد إضافة الميزات في `docs/ARCHITECTURE.md`. وشرح مفتاح التوقيع والتحديث فوق النسخة المثبّتة في `docs/RELEASING.md`.

### البناء والتحديثات (عبر CI فقط، دون حاسوب)

يُبنى هذا المشروع بالكامل عبر GitHub Actions. عند رفع `MagicRDR-project.zip` جديد إلى جذر المستودع يفكّه الـ workflow داخل شجرة المصدر، ثم يشغّل الاختبارات، ويبني الـ APK بـ `.NET 10 MAUI`، ويوقّعه بالمفتاح الدائم، وينشر GitHub Release. التفاصيل في `docs/UPDATING.md`.

### الشكر والإسناد

- **Magic-RDR (الأداة المكتبية) والعمل الأصلي على صيغ الملفات:** [ImFoxxyyy](https://github.com/ImFoxxyyy)
- **منفذ أندرويد (Magic RDR for Android):** [Zlatan-RDR](https://github.com/Zlatan-RDR) (حمزة)

### الترخيص

[MIT](LICENSE). حقوق الأداة الأصلية لـ ImFoxxyyy، وحقوق منفذ أندرويد لـ Zlatan-RDR (حمزة).

</div>
