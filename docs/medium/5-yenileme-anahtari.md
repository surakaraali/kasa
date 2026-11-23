# Yenileme anahtarı ikinci kez gelirse: rotasyon ve aile iptali

Kasa'da kullanıcı giriş yaptığında iki anahtar alıyor. Erişim anahtarı 15 dakika geçerli bir JWT. Süresi dolunca tarayıcı arka
planda yenisini istiyor. Bunu yapan yenileme anahtarı ise 14 gün yaşıyor.

Kimlik tarafını kurarken cevaplamak istediğim soru şuydu: Bu 14 günlük anahtarın bir kopyası başka birinin eline geçerse ne
olur? Kopya sessizce çalışmaya devam ederse kullanıcının bundan haberi bile olmaz.

## Her anahtar bir kez çalışıyor

Yenileme anahtarı 32 rastgele bayttan oluşuyor. Veritabanında yalnız SHA-256 özeti duruyor, yani tablo sızsa bile içindeki
değerler çereze yazılamaz. Şifrelerde yavaş bir özet gerekir, çünkü insanlar tahmin edilebilir şifreler seçer. Bu anahtar
rastgele olduğu için SHA-256 yetiyor. `/api/auth/refresh` her çağrıda eski anahtarı kullanılmış işaretleyip yenisini veriyor. Buna
rotasyon deniyor. Aynı girişten doğan bütün anahtarlar ortak bir `FamilyId` taşıyor. Ben buna aile diyorum.

Rotasyon çalınmayı görünür yapıyor. Gerçek kullanıcı önce yenilerse saldırganın elindeki kopya kullanılmış olur. Saldırgan önce
yenilerse bu kez gerçek kullanıcı eski anahtarla gelir. İki durumda da aynı anahtar sunucuya ikinci kez ulaşır. Sunucu hangi
tarafın saldırgan olduğunu bilemez. Bu yüzden ailenin bütün anahtarlarını iptal ediyorum ve iki taraf da oturumdan düşüyor. Çıkış yapmak da aynı aileyi
kapatıyor. Şifre değişince kullanıcının bütün aileleri iptal ediliyor.

## Kontrol ve yazma tek komutta

Önce satırı okuyup `UsedAt` boş mu diye baksaydım, sonra ayrı bir komutla yazsaydım, aynı anda gelen iki istek de "boş" görürdü.
İkisi de yeni anahtar alırdı. Kontrolü ve yazmayı tek `UPDATE` içine koydum:

```csharp
var claimed = await db.RefreshTokens
    .Where(t => t.Id == token.Id && t.UsedAt == null)
    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
if (claimed == 0)
{
    await RevokeFamilyAsync(token.FamilyId, ct);
    logger.LogWarning("Refresh token reuse for user {UserId}; family {FamilyId} revoked", token.UserId, token.FamilyId);
    return (RefreshOutcome.ReuseDetected, null, null);
}
```

Bunun SQL karşılığı `UPDATE ... WHERE Id = @id AND UsedAt IS NULL`. SQL Server aynı satıra gelen iki güncellemeyi sıraya koyuyor.
İlki satırı değiştiriyor ve 1 sonucunu alıyor. İkincisi sırası geldiğinde `UsedAt` dolu olduğu için 0 satır değiştiriyor. Kodda
0 sonucu tekrar kullanım anlamına geliyor.

`A_refresh_token_works_once_and_a_replayed_one_signs_the_whole_session_out` testi senaryoyu sırayla oynatıyor. Gerçek kullanıcı
yeniliyor. Saldırgan eski kopyayı gönderip 401 alıyor. Ardından gerçek kullanıcının en yeni anahtarı da 401 alıyor. İki isteğin
gerçekten aynı anda geldiği ayrı bir testim yok. O durumda dayandığım şey tek `UPDATE` komutu.

Bu kural istemciyi de bağlıyor. İki sekme aynı çerezle aynı anda yenilerse kaybeden taraf hırsız sayılır ve kullanıcı oturumdan
düşer. Bunu önlemek için web tarafında yenileme tek seferde yapılıyor. Sekme içinde ortak bir promise var, sekmeler arasında
`navigator.locks` kullanıyorum. İkinci sekme ilkini bekliyor ve yenilenmiş çerezi gönderiyor.

## Çerez neden httpOnly

```csharp
http.Response.Cookies.Append(Name, token, new CookieOptions
{
    HttpOnly = true,
    Secure = http.Request.IsHttps,
    SameSite = SameSiteMode.Strict,
    Path = "/api/auth",
    MaxAge = TimeSpan.FromDays(days),
});
```

`HttpOnly` sayesinde sayfaya sızan bir betik bu anahtarı `document.cookie` ile okuyamıyor. `SameSite=Strict` başka bir siteden
başlayan isteklere çerezin eklenmesini engelliyor. `Path` çerezi yalnız `/api/auth` adreslerine gönderiyor. Erişim anahtarı ise
tarayıcıda yalnız bellekte duruyor. `The_refresh_cookie_is_httponly_strict_and_limited_to_the_auth_path` testi bu üç özelliği
kontrol ediyor.

## Bu tasarımın bedeli

- **Kaybolan cevap kullanıcıyı dışarı atıyor.** Sunucu anahtarı döndürür ama cevap tarayıcıya ulaşmazsa elde eski çerez kalır.
  Sonraki yenileme tekrar kullanım sayılır ve kullanıcı yeniden giriş yapar. Bunu yumuşatacak bir ek süre koymadım.
- **İptal, verilmiş JWT'ye uzanmıyor.** Aile iptal edilse de elde kalan erişim anahtarı süresi bitene kadar çalışıyor. Bu süre
  15 dakika, üstüne doğrulamadaki 30 saniyelik saat toleransı ekleniyor.
- **Sayfaya sızan betik oturumu yine kullanabilir.** Çerezi okuyamasa da `/api/auth/refresh` adresine istek atıp cevaptaki
  erişim anahtarını alabilir. Bu yüzden XSS'i ayrıca önlemek gerekiyor. Web tarafının CSP'sinde hâlâ
  `'unsafe-inline'` var ve bunu eksik olarak not ettim.
