# Sprint sonu soruları

Her sprintin sonunda eklenir. Önce kendi cümlelerinle cevapla, sonra ipucuna bak. İpucu tam cevap değil;
mülakatta söylenmesi gereken ana fikir.

---

## Sprint 0: Hazırlık

**1. Neden her şeyi mikroservis yapmadık da sadece ERP aktarımını ayırdık?**
<details><summary>İpucu</summary>
Mikroservis her serviste ayrı veritabanı, ağ çağrısı, dağıtık hata ve izleme yükü getirir. Küçük ekip için bedeli faydasından
büyük. ERP aktarımını ayırmanın gerçek sebepleri var: ERP yavaş ve kapanabiliyor, ana uygulama onu beklememeli, ERP kapalıyken
iş kuyrukta beklemeli. Gerisi tek uygulamada ama modül sınırlarıyla (karar 0001).
</details>

**2. Neden .NET 11 değil de .NET 10?**
<details><summary>İpucu</summary>
.NET 10 uzun destekli (LTS, Kasım 2028'e kadar), .NET 11 kısa destekli (STS). Ürün ekipleri genelde LTS'te kalır. Paketler de
10.0.x'te sabit; NuGet'in "en son"u 11.x olduğu için kendiliğinden yükselmesin diye sürümler tek dosyada (karar 0002).
</details>

**3. `Directory.Packages.props` ne işe yarıyor? Olmasaydı ne olurdu?**
<details><summary>İpucu</summary>
Merkezi paket yönetimi: bütün paket sürümleri tek dosyada. Olmasa altı proje zamanla aynı paketin farklı sürümlerini kullanır,
"bende çalışıyor" sorunları başlar. Bir projede `Version=` yazılırsa derleme hata verir (NU1008).
</details>

**4. Redis'te `volatile-lru` yerine `allkeys-lru` seçseydik kilitler için ne tehlike olurdu?**
<details><summary>İpucu</summary>
`allkeys-lru` bellek dolunca herhangi bir anahtarı silebilir; süresi dolmamış bir senkron kilidi de silinebilir, iki sunucu
aynı bağlantıyı aynı anda çeker. `volatile-lru` sadece süresi olan anahtarları siler; bizde hepsinin süresi var ve önbellek
anahtarları kilitlerden önce gider.
</details>

**5. Docker'da servislere neden bellek sınırı koyduk?**
<details><summary>İpucu</summary>
Makinede 8 GB RAM var. Sınırsız bırakılırsa SQL Server bulduğu belleği alır, IDE ve tarayıcı yavaşlar. Her servisin sınırı
var; SQL Server'a ayrıca kendi iç sınırı (`MSSQL_MEMORY_LIMIT_MB`) verildi, çünkü konteyner sınırını aşan bir SQL Server
öldürülür, kendi sınırını bilen SQL Server ise önbelleğini küçültür.
</details>

**6. `/health` ne işe yarıyor?**
<details><summary>İpucu</summary>
Docker'ın, yük dengeleyicinin ve izleme panelinin "bu servis çalışıyor mu?" sorusuna cevap. Şimdilik sadece sürecin ayakta
olduğunu söylüyor; Sprint 6'da SQL Server, Redis, MongoDB ve RabbitMQ kontrolleri de ekleniyor.
</details>

---

## Sprint 1: Kimlik ve şirketler

**1. Neden yenileme anahtarını JSON gövdesinde değil de httpOnly bir çerezde gönderiyoruz?**
<details><summary>İpucu</summary>
httpOnly çerezi sayfadaki JavaScript okuyamaz; sayfaya bir betik sızsa (XSS) bile uzun ömürlü anahtarı çalamaz.
`SameSite=Strict` başka bir sitenin isteğine çerezin eklenmesini engeller (CSRF). Yolu `/api/auth`, başka isteklere gitmez.
Kısa ömürlü erişim anahtarı ise sadece bellekte.
</details>

**2. Yenileme anahtarı rotasyonu nedir, "çalınma tespiti" nasıl çalışıyor?**
<details><summary>İpucu</summary>
Her yenileme anahtarı bir kez çalışır; kullanınca yenisi gelir. Kullanılmış bir anahtar tekrar gelirse ya kullanıcı ya
saldırgan bir kopyayı kullanıyor demektir; hangisi olduğunu bilemeyiz, bu yüzden o oturumun bütün anahtarlarını (aile) iptal
ederiz. Testi var: çalınan kopya kullanılınca gerçek kullanıcının en yeni anahtarı da çalışmıyor.
</details>

**3. İki sekme aynı anda aynı yenileme anahtarını gönderirse ne olur?**
<details><summary>İpucu</summary>
`UPDATE ... SET UsedAt = now WHERE Id = @id AND UsedAt IS NULL` tek bir sorgu; sadece biri 1 satır günceller, diğeri 0.
Kaybeden tekrar kullanım sayılır. Bu yüzden web istemcisi yenilemeyi tek seferde, sıraya koyarak yapıyor.
</details>

**4. Veritabanında neden yenileme anahtarının kendisini değil SHA-256'sını tutuyoruz? Şifre için neden SHA-256 yetmez?**
<details><summary>İpucu</summary>
Veritabanı sızarsa elde çalışan anahtar olmaz. Anahtar 256 bit rastgele, tahmin edilemez; hızlı bir hash yeter. Şifreleri
insanlar seçer, tahmin edilebilir; onlar için yavaş, tuzlu bir hash (Identity'nin PBKDF2'si) gerekir.
</details>

**5. Bilinmeyen bir e-posta ile giriş yapılınca neden yine de sahte bir şifre doğrulaması yapıyoruz?**
<details><summary>İpucu</summary>
Yapmasak "böyle kullanıcı yok" cevabı hızlı, "şifre yanlış" cevabı yavaş döner; aradaki süre farkıyla hangi e-postaların
kayıtlı olduğu bulunabilir. Mesaj da aynı: "E-posta ya da şifre hatalı."
</details>

**6. Bir şirketin başka bir şirketin verisini görmesini nasıl engelliyorsun?**
<details><summary>İpucu</summary>
EF Core global sorgu filtresi: şirket verisi olan her tabloya `CompanyId == token'daki şirket` şartı kendiliğinden ekleniyor.
Şirket kimliği sadece token'dan. Bilinmiyorsa hiçbir şey dönmüyor. `SaveChanges` başka şirketin satırını yazmayı reddediyor.
Başka şirketin kaydı 404 (var olduğu bile belli olmasın) (karar 0004).
</details>

**7. Kullanıcı tablosunda neden şirket filtresi yok?**
<details><summary>İpucu</summary>
Giriş yaparken şirket henüz bilinmiyor; filtre olsaydı hiçbir kullanıcı bulunamazdı. Bu yüzden kullanıcı listeleyen yönetici
ekranı şirketi açıkça yazıyor. İstisna bilinçli ve görünür.
</details>

**8. Redis çökerse giriş denemesi sınırı ne olur? Bu bir güvenlik açığı mı?**
<details><summary>İpucu</summary>
Sınır atlanır, giriş çalışmaya devam eder (fail-open), logda uyarı çıkar. Açık sayılmaz çünkü asıl koruma hesap başına kilitleme
(5 hatalı denemede 15 dakika) ve o SQL'de. Önbellek çöktü diye giriş sayfasının kapanması daha kötü olurdu (karar 0005).
</details>

**9. Kilitli hesap neden 401 değil de 423 dönüyor? Bunun bir bedeli var mı?**
<details><summary>İpucu</summary>
Kullanıcıya neden giremediğini ve ne kadar bekleyeceğini söylemek için. Bedeli: 423 o e-postanın kayıtlı olduğunu belli eder.
Kullanılabilirlik için kabul edilmiş bir ödünleşim; karar notunda yazılı.
</details>

**10. Yeni bir uç nokta yazıp `[Authorize]` eklemeyi unutursan ne olur?**
<details><summary>İpucu</summary>
Varsayılan politika (fallback) "giriş yapmış olmak". Unutulan uç nokta açık kalmaz; açık olması gerekenler `[AllowAnonymous]`
ile bilerek açılıyor.
</details>

---

## Sprint 2: Banka entegrasyonu

**1. PKCE ne işe yarıyor? State zaten varken neden ikisi birden?**
<details><summary>İpucu</summary>
İkisi farklı şeyi koruyor. State, bankadan dönen cevabı bizim başlattığımız isteğe (ve tarayıcıya) bağlıyor; sahte bir
geri dönüşü (CSRF) engelliyor. PKCE, bankanın verdiği kodu bizim sunucumuza bağlıyor: kodu kaydederken bankaya doğrulayıcının
SHA-256 özetini verdik, kodu anahtara çevirirken doğrulayıcının kendisini veriyoruz. Kod yolda çalınsa doğrulayıcı olmadan
işe yaramaz.
</details>

**2. Geri dönüş isteğinde kullanıcı oturumu yok. Şirketi nereden biliyorsun? Bu bir açık değil mi?**
<details><summary>İpucu</summary>
Şirketi state'in işaret ettiği bağlantıdan buluyorum; state'in sadece özeti veritabanında, tek kullanımlık. Ama state tek
başına yetmez: saldırgan kendi şirketi için akışı başlatıp onay bağlantısını kurbana yollayabilir. Kurban onaylarsa kurbanın
banka verisi saldırganın şirketine akar. Bu yüzden akışı başlatan tarayıcıya httpOnly bir çerez koyuyorum ve geri dönüşte
state ile karşılaştırıyorum. Başka tarayıcıdan gelen geri dönüş reddediliyor (testi var).
</details>

**3. O çerez neden `SameSite=Strict` değil de `Lax`?**
<details><summary>İpucu</summary>
Bankadan Kasa'ya dönüş siteler arası bir üst seviye GET yönlendirmesi. `Strict` çerez siteler arası hiçbir istekte gitmez,
çerez gelmezdi. `Lax` üst seviye GET'te gider, siteler arası POST ve gömülü isteklerde gitmez. Çerezin yolu da sadece geri
dönüş adresi, süresi 15 dakika.
</details>

**4. Banka anahtarlarını ve SOAP şifresini nasıl saklıyorsun? Veritabanı yedeği çalınırsa ne olur?**
<details><summary>İpucu</summary>
ASP.NET Core Data Protection ile şifreli. Anahtar halkası da SQL Server'da ki birden fazla API örneği aynı veriyi çözebilsin.
Dürüst cevap: halka şu an şifresiz, aynı veritabanında; yedeği alan ikisini birden alır. Üretimde halkayı bir sertifikayla ya
da anahtar kasasıyla sarmak gerekir (`ProtectKeysWithCertificate`, Key Vault). Kodda `shortcut:` notu ve karar 0006'da yazıyor.
</details>

**5. Aynı hareketin iki kez kaydedilmeyeceğini nasıl garanti ediyorsun?**
<details><summary>İpucu</summary>
Veritabanında `(BankAccountId, BankReference)` benzersiz indeksi. Uygulama önce kayıtlı referanslara bakıp yenileri yazıyor;
arada başka bir çalışma yazdıysa indeks hata veriyor (2601/2627), senkron tekrar okuyup kalanı yazıyor. Redis kilidi ek; asıl
garanti indeks. Redis kapalıyken altı senkronu aynı anda çalıştıran test var.
</details>

**6. Referans sütununda neden ikili (BIN2) karşılaştırma?**
<details><summary>İpucu</summary>
SQL Server'ın varsayılan karşılaştırması büyük/küçük harfe duyarsız. Bankanın "ab12" ve "AB12" referansları iki ayrı hareket
olabilir; duyarsız indeks ikincisini "zaten var" sayıp atlar, para kaybolur. BIN2 bayt bayt karşılaştırır. Uygulamadaki
küme de `StringComparer.Ordinal`, ikisi aynı kuralda.
</details>

**7. Redis kilidini nasıl alıp bırakıyorsun? Neden bırakırken Lua betiği?**
<details><summary>İpucu</summary>
`SET anahtar rastgele-değer NX PX 300000`: yoksa koy, 5 dakika süreli. Bırakırken düz `DEL` tehlikeli: benim işim 5 dakikayı
aştıysa kilit çoktan düştü, başka biri aldı; `DEL` onunkini siler. Betik "değer hâlâ benimse sil" kontrolünü tek adımda yapıyor.
</details>

**8. Banka kapalıysa, yavaşsa, bozuk veri gönderirse, rıza iptal edildiyse ne oluyor?**
<details><summary>İpucu</summary>
Üç tür. Ulaşılamıyor (503, 10 sn zaman aşımı): hata kaydedilir, bağlantı etkin kalır, sonraki çalışma telafi eder. Reddedildi
(rıza iptal, şifre değişmiş): bağlantı "yeniden bağlanmalı" olur, zamanlayıcı artık denemez. Bozuk veri: o hesabın cevabının
tamamı reddedilir, hiçbir satırı yazılmaz; yarım ekstre, eksiksiz görünen yanlış ekstredir. Erişim anahtarı süresi dolduysa
(401) bir kez yenileyip tekrar deniyorum. Hepsinin testi var.
</details>

**9. "1250.50" tutarını neden kabul etmiyorsun? Sadece bir nokta.**
<details><summary>İpucu</summary>
KlasikBank Türkçe biçim gönderiyor: binlik ayırıcı nokta, ondalık virgül ("1.250,50"). "1250.50"yi bu kurala göre gevşek
okursan nokta binlik ayırıcı sayılır, 125.050 TL olur. Bu yüzden tutar sadece katı kalıba uyuyorsa okunuyor. Regex'te `\d`
değil `[0-9]`: .NET'te `\d` başka alfabelerin rakamlarını da kabul ediyor.
</details>

**10. Hareketler listesinde neden OFFSET değil de keyset sayfalama?**
<details><summary>İpucu</summary>
OFFSET 10000 veritabanına 10000 satırı okuyup atlatır; sayfa ilerledikçe yavaşlar. Arada yeni hareket gelirse satırlar
sayfalar arasında kayar, biri iki kez görünür. Keyset "son gördüğümden sonrakiler" der: `(BookedAt, Id)` sırası, imleç son
satırın değerleri. İndeks `(CompanyId, BookedAt, Id)`; 1000. sayfa 1. sayfa kadar hızlı.
</details>

**11. Quartz'ı neden bellek deposuyla kullandın? Üç API örneği olsa ne olur?**
<details><summary>İpucu</summary>
Her örnek kendi zamanlayıcısını çalıştırır, aynı bağlantıyı üçü birden çekmeye kalkar. Redis kilidi birini geçirir, diğerleri
çekilmeden geçer; kilit yoksa bile benzersiz indeks çift kaydı önler. Kalıcı Quartz deposu (cluster) gerekmedi çünkü kaçan bir
çalışma 15 dakika sonra telafi ediliyor. `[DisallowConcurrentExecution]` aynı örnekte işin üst üste binmesini önlüyor.
</details>

**12. Ham cevapları neden MongoDB'ye yazıyorsun, neden SQL Server'a değil? Arşive ne girmiyor?**
<details><summary>İpucu</summary>
Şekli bankadan bankaya değişen, bir kez yazılan, nadiren okunan ve 90 gün sonra kendiliğinden silinmesi gereken veri: belge
veritabanı ve TTL indeksi tam buna göre. Ana SQL veritabanı ve yedekleri şişmiyor. Arşive girmeyen: anahtar çağrıları (cevapta
anahtar var), istek başlıkları (Bearer orada) ve SOAP isteği (şifre orada). MongoDB kapalıysa senkron devam ediyor.
</details>
