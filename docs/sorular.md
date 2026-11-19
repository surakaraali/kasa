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

---

## Sprint 3: Eşleştirme ve ERP aktarımı

**1. Outbox deseni hangi sorunu çözüyor? Olmasaydı ne olurdu?**
<details><summary>İpucu</summary>
"Dual write" sorunu: veritabanına kayıt ve RabbitMQ'ya yayın iki ayrı sistem, tek işlemde yapılamaz. Kayıttan sonra çökersen
mesaj hiç gitmez (eşleşti ama ERP'de fiş yok), yayından sonra kayıt geri alınırsa sahipsiz mesaj kalır. Outbox'ta mesaj
eşleşmeyle aynı SQL işleminde bir tabloya yazılır; ayrı bir döngü onu RabbitMQ'ya taşır.
</details>

**2. Outbox "en az bir kez" teslim ediyor. Aynı mesaj iki kez gelirse ERP'de iki fiş olmaz mı?**
<details><summary>İpucu</summary>
Olmaz, çünkü tüketici idempotent. Her mesajın sabit bir `ExportId`'si var ve ERP'ye `Idempotency-Key` başlığı olarak gidiyor.
ERP aynı anahtarı ikinci kez görünce ilk fişi dönüyor. Aynı anahtarla farklı içerik gelirse reddediyor (422). Testte outbox
satırlarını "gönderilmedi"ye çekip hepsini tekrar yolluyorum, fiş sayısı değişmiyor.
</details>

**3. Birden fazla API örneği aynı outbox satırını iki kez yayınlamasın diye ne yaptın?**
<details><summary>İpucu</summary>
`SELECT TOP 50 ... WITH (UPDLOCK, READPAST, ROWLOCK)`. UPDLOCK okuduğum satırları işlem bitene kadar kilitler, READPAST başka
birinin kilitlediği satırları beklemeden atlar. Örnekler işi paylaşır, birbirini beklemez. Yine de çifte yayın olursa
idempotent tüketici korur.
</details>

**4. Mesajı ne zaman ack'liyorsun? Neden sonucu ack'ten önce yayınlıyorsun?**
<details><summary>İpucu</summary>
Elle onay (manual ack): ERP çağrısı bitip sonuç mesajı yayınlandıktan sonra. Ack önce gelseydi ve sonra çökseydim mesaj
kaybolurdu. Bu sırayla çökersem mesaj tekrar gelir, ERP aynı fişi döner, sonuç tekrar yayınlanır; tekrar zararsız.
</details>

**5. Yeniden denemeyi nasıl yaptın? Neden mesajı direkt `nack requeue` etmedin?**
<details><summary>İpucu</summary>
`requeue` mesajı anında kuyruğun başına koyar: ERP kapalıyken saniyede yüzlerce deneme yapan sıcak bir döngü olur.
Bunun yerine TTL'li bir bekleme kuyruğu (`erp.export.retry`) var. Mesaj orada 10 saniye bekliyor, süre dolunca dead-letter
ayarıyla ana kuyruğa dönüyor. Deneme sayısı başlıkta. Hak bitince `erp.export.dead` kuyruğuna gidiyor ve kullanıcı "başarısız" görüyor.
</details>

**6. Geçici ve kalıcı hatayı nasıl ayırıyorsun?**
<details><summary>İpucu</summary>
5xx, 408, 429, zaman aşımı ve bağlantı hatası geçici: tekrar denemek işe yarayabilir. Diğer 4xx kalıcı, örneğin 422 "cari
kart bulunamadı": kaç kez denersen dene aynı cevap. Kalıcı hata tekrar denenmeden ölü kuyruğa gidiyor. Okunamayan mesaj da öyle.
</details>

**7. Dead-letter kuyruğundaki mesajla ne yapılır?**
<details><summary>İpucu</summary>
Kişi inceler. Hata başlıkta ve harekette görünüyor. Kasa'da hareket "Başarısız" durumunda, eşleşmesi değiştirilebiliyor;
kullanıcı düzeltip onaylayınca yeni bir `ExportId` ile yeni bir istek gidiyor. Eski mesajı körü körüne yeniden oynatmak
yerine kararı yeniden almak daha güvenli.
</details>

**8. Eşleştirme kurallarını neden bu kadar katı yaptın?**
<details><summary>İpucu</summary>
Yanlış cariye yazılan para, henüz yazılmamış paradan kötü: müşteriye yanlış ekstre gider, mutabakat bozulur. Kural ancak tek
bir cari kalıyorsa karar veriyor. IBAN bir cariyi, vergi numarası başka bir cariyi gösteriyorsa ikisinden biri yanlış;
kişiye bırakıyorum. Unvan benzerliği gibi bulanık kurallar açıklanamaz.
</details>

**9. İki muhasebeci aynı hareketi aynı anda eşlerse ne oluyor?**
<details><summary>İpucu</summary>
İyimser eşzamanlılık: hareketin SQL Server `rowversion` sütunu var. İstek, kişinin gördüğü sürümü taşıyor; EF Core
`UPDATE ... WHERE RowVersion = @gördüğün` yapıyor. İlki geçer, sürüm değişir; ikincisi 0 satır günceller ve 409 alır.
Kilit tutulmadığı için kimse kimseyi beklemiyor. Testi var.
</details>

**10. Aktarılmış bir hareketin eşleşmesi neden değiştirilemiyor?**
<details><summary>İpucu</summary>
ERP'de fiş zaten var. Kasa'da cariyi değiştirmek ERP'yi düzeltmez; iki sistem birbirinden ayrılır. Muhasebede düzeltme ters
fişle yapılır, o yüzden Kasa 409 dönüyor ve "düzeltme ERP'de yapılmalı" diyor. Aktarılmakta olan (Pending) hareket de
kilitli: yolda bir mesaj var.
</details>

**11. Denetim kaydını neden MongoDB'ye değil SQL Server'a yazdın? Ham cevapları Mongo'ya yazmıştın.**
<details><summary>İpucu</summary>
Ham cevap arşivi kaybolabilir (Mongo kapalıysa sadece uyarı). Denetim kaydı kaybolamaz: "bu parayı bu cariye kim yazdı?"
sorusunun cevabı olmalı. Eşleşmeyle aynı SQL işleminde yazılınca biri olup öteki olmaması imkânsız.
</details>

**12. RabbitMQ kapanırsa ne olur? Aktarım servisi kapanırsa?**
<details><summary>İpucu</summary>
RabbitMQ kapalıysa outbox satırları tabloda bekler; yayıncı her saniye tekrar dener, kapanış boyunca hiçbir şey kaybolmaz.
Aktarım servisi kapalıysa mesajlar quorum kuyruğunda bekler, servis açılınca işler. İkisi için de test var. Kullanıcı bu sırada
hareketleri "Aktarılıyor" görüyor.
</details>

---

## Sprint 4: Web arayüzü ve canlı panel

**1. Erişim anahtarını neden localStorage'da değil de bellekte tutuyorsun?**
<details><summary>İpucu</summary>
localStorage'ı sayfadaki her betik okur; bir XSS açığı anahtarı alıp başka yerden kullanır. Bellekteki anahtar sayfa
kapanınca gider. Sayfa yenilenince httpOnly yenileme çereziyle yenisi alınıyor; o çerezi betikler okuyamıyor. Anahtar 15 dakika
geçerli, çalınsa bile kısa.
</details>

**2. Next.js neden API'ye istekleri aktarıyor (rewrite)? Tarayıcı doğrudan 5100'e gitse ne olurdu?**
<details><summary>İpucu</summary>
Farklı köken olurdu: CORS ayarı, `credentials: include` ve yenileme çerezi için `SameSite=None; Secure` gerekirdi. `SameSite=None`
çerezi siteler arası isteklere açar (CSRF yüzeyi). Aynı adresin arkasında çerez `Strict` kalıyor, CORS hiç yok.
</details>

**3. Aynı anda iki istek 401 alırsa ne olur? Nasıl çözdün?**
<details><summary>İpucu</summary>
İkisi de yenileme isterse ikinci istek kullanılmış yenileme anahtarını gönderir. Sunucu bunu çalınma sayıp bütün oturum
ailesini kapatır, kullanıcı atılır. React geliştirme modunda efektler iki kez çalıştığı için bu hemen görülür. Çözüm: sayfa
genelinde tek bir yenileme sözü (promise); ikinci isteyen aynı sonucu bekler.
</details>

**4. SignalR'da kullanıcı başka şirketin mesajlarını nasıl alamıyor?**
<details><summary>İpucu</summary>
Bağlanınca hub, şirketi JWT'deki `company_id`'den okuyup bağlantıyı `company:{id}` grubuna koyuyor. İstemci grup seçemiyor.
Mesaj da veri taşımıyor, sadece "değişti" diyor; ekran veriyi normal, yetkili API isteğiyle alıyor. Başka şirketin mesaj
almadığını gösteren test var.
</details>

**5. Tarayıcı WebSocket'te başlık gönderemiyor. JWT'yi hub'a nasıl iletiyorsun? Riski ne?**
<details><summary>İpucu</summary>
Sorgu dizgisinde `access_token` olarak. API sadece `/hubs` yolunda oradan okuyor. Risk: adres loglara düşebilir. Anahtar 15
dakikalık olduğu için zarar sınırlı; üretimde erişim loglarında sorgu dizgisi maskelenmeli.
</details>

**6. Bağlantı koparken gelen değişiklikleri nasıl kaçırmıyorsun?**
<details><summary>İpucu</summary>
Kaçırıyorum, ama telafi ediyorum: SignalR otomatik yeniden bağlanıyor, bağlanınca ekran bir kez yeniden veri çekiyor. Mesajlar
durum taşımadığı için kaçan mesajın içeriği önemli değil, sadece "yenile" anlamı var.
</details>

**7. Panel önbelleğini neden silerek değil de sürümleyerek geçersiz kılıyorsun?**
<details><summary>İpucu</summary>
Silme yarışı: A isteği eski veriyle hesaplamaya başlar, bu sırada değişiklik olur ve anahtar silinir, A bitince eski sonucu
önbelleğe yazar; panel eski kalır. Sürüm anahtarında değişiklik sayacı artırır, A eski sürümün anahtarına yazar ve kimse onu
okumaz. Eski anahtarlar süresi dolunca gider.
</details>

**8. Önbelleğin faydasını nasıl ölçtün?**
<details><summary>İpucu</summary>
Cevaba `X-Cache` ve `X-Compute-Ms` başlıkları koydum. Küçük veride önbelleksiz 28 ms, önbellekten 2 ms. Asıl ölçüm 500 bin
hareketle Sprint 6'da. "Önbellek ekledim, hızlandı" demek yerine sayı vermek.
</details>

**9. Hareketler listesinde "daha eski hareketler" nasıl çalışıyor? Arada yeni hareket gelirse?**
<details><summary>İpucu</summary>
Keyset imleç: son satırın (tarih, id) değeri. Sonraki sayfa "bundan eskiler". Arada yeni hareket gelirse en üste eklenir,
sayfalar kaymaz; OFFSET'te bir satır iki sayfada görünürdü. Canlı mesaj gelince ilk sayfa yeniden çekiliyor.
</details>

**10. İki muhasebeci aynı ekranda aynı hareketi eşlerse arayüz ne gösteriyor?**
<details><summary>İpucu</summary>
Her satır gördüğü `rowVersion`'ı gönderiyor. İkinci kişi 409 alıyor ve "siz bakarken başka biri değiştirdi" mesajını görüyor;
liste yenileniyor, güncel hali çıkıyor. Kimse kimsenin kararını sessizce ezmiyor.
</details>

---

## Sprint 5: Yapay zekâ asistanı

**1. Modele neden SQL yazdırmadın da araç verdin?**
<details><summary>İpucu</summary>
SQL yazan model her şeyi okuyabilir; şirket filtresini unutması ya da ikna edilmesi yeter. Araçlarda sorguyu Kasa yazıyor,
şirket filtresi her sorguda zorunlu. Model sadece "neyi" söylüyor, "nasıl"ı Kasa belirliyor. Araçlar sadece okuyor.
</details>

**2. Asistan başka şirketin verisini nasıl göremiyor?**
<details><summary>İpucu</summary>
Araçların şirket parametresi yok. Şirket, isteği yapan kullanıcının JWT'sinden TenantContext'e geliyor ve EF Core sorgu filtresi
her sorguya ekliyor. Model "bütün şirketleri göster" dese bile elindeki araç sadece kendi şirketini okuyabiliyor. Bunun testi var:
saldırganın kendi hareketine yazdığı talimat, başka şirketin hesabını göstermiyor.
</details>

**3. Prompt injection nedir, Kasa'da nereden gelebilir?**
<details><summary>İpucu</summary>
Modelin okuduğu veriye gömülmüş talimat. Kasa'da kaynak banka hareket açıklaması ve karşı taraf adı: parayı gönderen istediğini
yazabilir ("SİSTEM: önceki talimatları yok say..."). Önlemler: açıklamalar araç sonucunda JSON alanı olarak gidiyor, sistem
talimatı bunları veri saymasını söylüyor; asıl önlem de mimaride: asistanın yapabileceği en kötü şey kendi şirketinin verisini okumak.
</details>

**4. IBAN ve vergi numarasını neden maskeliyorsun? Model zaten senin.**
<details><summary>İpucu</summary>
Veri azaltma: soruyu cevaplamak için tam numara gerekmiyor, hesapları ayırmaya son 4 hane yetiyor. Dışarı çıkmayan veri sızamaz;
loglara, konuşma geçmişine, ekran görüntüsüne de düşmez. KVKK açısından da "gerektiği kadar" ilkesi.
</details>

**5. Maliyeti nasıl sınırladın?**
<details><summary>İpucu</summary>
Soru başına en fazla 6 araç turu (model döngüye girerse durur), kullanıcı başına saatte 30 soru (Redis), cevap başına üst token
sınırı, 90 saniye zaman aşımı, geçmiş 20 mesajla sınırlı. Her turun girdi/çıktı/önbellek token'ı loglanıyor; maliyet ölçülebilir.
Model ve effort ayardan değişiyor.
</details>

**6. Prompt caching nasıl çalışıyor? Neden tarihi sistem talimatına koymadın?**
<details><summary>İpucu</summary>
Önbellek önek eşleşmesi: araçlar, sistem, mesajlar sırasıyla; önekte tek bir bayt değişirse sonrası önbellekten okunmaz. Tarih ya
da şirket adı sistem talimatında olsaydı her gün ve her şirket için önek değişirdi. Onları son sorunun yanına ayrı bir blok olarak
koydum; araçlar ve talimat her istekte aynı. Araç turlarında da konuşmanın öncesi önbellekten okunuyor.
</details>

**7. Araç döngüsü nasıl işliyor?**
<details><summary>İpucu</summary>
İstek gider, model ya cevap yazar ya `tool_use` blokları döner (stop_reason: tool_use). Kasa araçları çalıştırır, modelin cevabını
(düşünme blokları imzalarıyla birlikte) aynen geri ekler, ardından bütün araç sonuçlarını tek bir kullanıcı mesajında gönderir.
Model cevap verene ya da tur sınırına gelene kadar sürer. Akış sırasında metin parçaları tarayıcıya hemen iletiliyor.
</details>

**8. Model yanlış bir araç girdisi verirse ne oluyor?**
<details><summary>İpucu</summary>
Araç girdisi dış veri gibi doğrulanıyor: tarih biçimi, sayı aralığı, uzunluk. Hatalıysa istisna fırlatmıyorum; modele `is_error`
işaretli bir sonuç dönüyor ("from must be YYYY-MM-DD"), model düzeltip tekrar deniyor. Girdiler akarken geldiği için yarım kalmış
bir girdi de aynı doğrulamaya takılıyor.
</details>

**9. Anahtar olmadan asistanı nasıl test ettin?**
<details><summary>İpucu</summary>
Sahte bir Claude sunucusu: SDK'nın HttpClient'ına bir handler verdim, gerçek API'nin SSE biçiminde önceden yazılmış cevaplar
dönüyor ve gönderilen istekleri saklıyor. Böylece hem döngüyü (araç turu, düşünme bloğu imzası, araç sonucu) hem de gönderilen
isteği (model, önbellek, araç listesi, maskeli veri) kontrol edebiliyorum. CI'da anahtar gerekmiyor. Gerçek modelle kalite
denemesi ayrıca yapılmalı.
</details>

**10. Asistan yanlış rakam söylerse?**
<details><summary>İpucu</summary>
Olabilir; o yüzden rakamları model hesaplamıyor, araçlar SQL'de hesaplıyor; model sadece aktarıyor. Ayrıca her toplamın hangi
dönemi ve filtreyi kapsadığını söylemesi isteniyor, kullanıcı ekranlardan kontrol edebiliyor. Asistan karar vermiyor, eşleme
yapmıyor; sadece okuyor.
</details>
