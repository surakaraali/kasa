# Güvenlik turu: OWASP Top 10 (2025)

Her madde için Kasa'da ne yapıldığı, nerede olduğu ve neyin bilerek eksik bırakıldığı. "Eksik" satırları dürüst bir liste:
üretime çıkmadan kapatılması gerekenler.

## A01 Bozuk erişim denetimi
- **Şirket izolasyonu:** Her şirket verisi EF Core sorgu filtresinden geçiyor; `SaveChanges` başka şirketin satırını yazmayı
  reddediyor (karar 0004). Şirket her zaman JWT'den, hiçbir zaman istekten. Başka şirketin kaydı 404.
- **Roller:** `Admin` ve `CanEdit` politikaları; geri kalan her uç nokta kimlik istiyor (fallback policy). Anonim uçlar
  bilerek işaretli: giriş, kayıt, davetle katılma, banka geri dönüşü, `/health`, `/alive`.
- **Banka geri dönüşü:** state tek kullanımlık ve akışı başlatan tarayıcının çerezine bağlı (karar 0006).
- **SignalR:** grup şirketten, şirket token'dan; istemci grup seçemiyor.
- **Asistan:** araçların şirket parametresi yok (karar 0013).
- Testler: hata senaryoları 11 ve 12.

## A02 Güvenlik yapılandırma hataları
- API: `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'`;
  üretimde HSTS. Hata cevapları problem details, yığın izi yok.
- Web: aynı başlıklar ve üretim derlemesinde CSP.
- Swagger ve OpenAPI sadece Development'ta.
- `/health` sadece durum ve kendi açıklamalarımızı veriyor; istisna metni ve sunucu adı yok.
- **Eksik:** Web CSP'si betikler için `'unsafe-inline'` içeriyor (Next.js nonce'suz); nonce'lu CSP gerekli.
  Aspire paneli kimliksiz, sadece `127.0.0.1`'e bağlı; üretimde böyle açılmamalı.

## A03 Yazılım tedarik zinciri
- Bütün NuGet sürümleri tek dosyada sabit (merkezi paket yönetimi, karar 0002); npm sürümleri tam sürüm ve `package-lock.json`
  ile, CI'da `npm ci`.
- Yeni çıkmış sürümler bilerek alınmadı (en az iki haftalık sürümler; örneğin OpenTelemetry 1.21 yerine 1.20).
- CI uyarıları hata sayıyor; biçim denetimi var.
- **Eksik:** Dependabot ya da `dotnet list package --vulnerable` / `npm audit` CI'da çalışmıyor.

## A04 Kriptografik hatalar
- Şifreler ASP.NET Core Identity ile (PBKDF2). Yenileme anahtarı ve davet anahtarı veritabanında sadece SHA-256 özeti.
- Banka erişim anahtarları, PKCE doğrulayıcısı ve SOAP şifresi Data Protection ile şifreli.
- JWT sadece HS256 kabul ediyor (algoritma karışıklığına kapalı), 15 dakika.
- **Eksik:** Data Protection anahtar halkası aynı veritabanında ve şifresiz (kodda `shortcut:` notu, karar 0006); üretimde
  sertifika ya da anahtar kasası. JWT imza anahtarı Development ayarında düz metin; üretimde gizli ayar deposundan gelmeli.

## A05 Enjeksiyon
- SQL: bütün sorgular EF Core üzerinden parametreli; tek ham sorgu (outbox) `FromSql` ile ve sadece sabit değerler içeriyor.
- XML: SOAP cevapları DTD kapalı okunuyor (XXE ve "billion laughs" kapalı). SOAP isteği `XElement` ile kuruluyor, değerler kaçışlı.
- HTML: test bankası ve test ERP'si sayfalarında değerler `HtmlEncode`'lu. React çıktıyı kendisi kaçışlıyor.
- Prompt injection: banka açıklamaları veri olarak gidiyor, araçlar sadece okuyor (karar 0013, senaryo 15).

## A06 Güvensiz tasarım
- Para ile ilgili her doğruluk garantisi veritabanında: çift kayıt benzersiz indeksle, eşzamanlı eşleme `rowversion` ile,
  eşleşme ile mesaj aynı işlemde (outbox).
- Belirsizlikte kişiye bırakan eşleştirme kuralları; aktarılmış hareket değiştirilemiyor.
- Dış veri (banka cevapları) katı kalıplarla doğrulanıyor; şüpheli cevap hiç kaydedilmiyor.
- Hata senaryoları tablosu tasarım aşamasında yazıldı, hepsi testli ([hata-senaryolari.md](hata-senaryolari.md)).

## A07 Kimlik doğrulama hataları
- Şifre kuralları (10 karakter, büyük/küçük harf, rakam). 5 hatalı denemede 15 dakika kilit. Adres başına dakikada 10 deneme.
- Bilinmeyen e-postaya da aynı sürede aynı cevap (sahte şifre özeti), hesap varlığı sızmıyor.
- Yenileme anahtarı tek kullanımlık, rotasyonlu; tekrar kullanılırsa bütün oturum ailesi iptal. Çerez httpOnly, `SameSite=Strict`,
  sadece `/api/auth` yolunda. Erişim anahtarı tarayıcıda bellekte.
- **Eksik:** İki adımlı doğrulama (MFA) yok. E-posta doğrulaması ve şifre sıfırlama yok.

## A08 Yazılım ve veri bütünlüğü
- Mesajlar yayıncı onayıyla (publisher confirms) ve `mandatory` ile gönderiliyor; tüketici elle ack'liyor; ERP'ye idempotency anahtarı.
- Düşünme blokları asistana imzasıyla, değiştirilmeden geri gönderiliyor.
- **Eksik:** Kuyruk mesajları imzalı değil; RabbitMQ'ya yazabilen biri sahte aktarım mesajı koyabilir. Üretimde RabbitMQ
  kullanıcıları ayrı ve yetkileri kuyruğa göre sınırlı olmalı.

## A09 Loglama ve uyarı hataları
- OpenTelemetry ile bütün servislerin izi, metrikleri ve yapılandırılmış logları tek panelde; bir isteğin izi kuyruk boyunca sürüyor.
- Güvenlik olayları loglanıyor: kilitlenen hesap, tekrar kullanılan yenileme anahtarı, Redis/Mongo/RabbitMQ kopması,
  ölü mektup kuyruğuna giden mesaj. Eşleme kararları denetim tablosunda (kim, ne zaman, hangi cari).
- Loglara sır yazılmıyor: banka anahtarları, şifreler ve istek başlıkları ne loglarda ne Mongo arşivinde.
- **Eksik:** Uyarı (alert) kuralı yok; panel var ama kimse "ölü mektup kuyruğu doluyor" diye uyarılmıyor. Hub'a giden
  JWT sorgu dizgisinde; erişim loglarında maskelenmeli.

## A10 İstisnai durumların yanlış ele alınması
- Her dış bağımlılığın kapanması için tanımlı davranış var ve testli: Redis, MongoDB, RabbitMQ, banka, ERP, Claude.
- Banka hataları üç türe ayrılıyor (geçici / reddedildi / bozuk veri), ERP hataları ikiye (geçici / kalıcı).
- Beklenmeyen istisna 500 problem details dönüyor, yığın izi dönmüyor; kuyruk tüketicisinde mesajı kuyruğa geri veriyor.
- Model araç girdisi hatalıysa istisna değil, modele hata sonucu.
