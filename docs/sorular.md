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
