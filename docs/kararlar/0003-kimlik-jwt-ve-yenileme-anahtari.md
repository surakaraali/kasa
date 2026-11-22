# 0003: Kimlik: ASP.NET Core Identity, kısa ömürlü JWT, rotasyonlu yenileme anahtarı

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Kullanıcılar şirket adına para hareketlerini görüyor ve eşliyor. Kimlik doğrulama hem güvenli olmalı hem de web arayüzü
ile SignalR'ın kullanabileceği bir yapıda olmalı. Şifreleri saklamak, kilitlemek ve doğrulamak kendi başına yazılmamalı.

## Seçenekler
1. **Identity'nin hazır uç noktaları (`MapIdentityApi`).** Hızlı. Ama ürettiği token'lar JWT değil, kendine özgü; yenileme
   anahtarının rotasyonu ve çalınma tespiti yok.
2. **Dış kimlik sağlayıcı (Keycloak, Auth0, Entra ID).** Gerçek bir üründe doğru seçim. Bu proje için ek bir sistem ve kurulum.
3. **Identity (kullanıcı deposu, şifre hash'i, kilitleme) + kendi token servisimiz.**

## Karar
**3. seçenek.**
- Şifreler Identity'nin PBKDF2 hash'iyle saklanıyor. 5 hatalı denemede hesap 15 dakika kilitleniyor.
- **Erişim anahtarı:** 15 dakikalık JWT, HMAC-SHA256. Yalnızca bu algoritma kabul ediliyor ("none" ya da başka algoritma reddedilir).
- **Yenileme anahtarı:** 32 rastgele bayt. Veritabanında sadece SHA-256'sı duruyor.
  - **Tek kullanımlık:** her kullanımda yenisi veriliyor (rotasyon).
  - **Çalınma tespiti:** kullanılmış bir anahtar tekrar gelirse, o oturumun bütün anahtarları (aile) iptal ediliyor.
  - İki istek aynı anahtarla aynı anda gelirse tek `UPDATE ... WHERE UsedAt IS NULL` hangisinin kazanacağını belirliyor.
- **Yenileme anahtarı httpOnly çerezde:** `SameSite=Strict`, yolu `/api/auth`. Sayfaya sızan bir betik onu okuyamıyor,
  başka bir site onu gönderemiyor. Erişim anahtarı tarayıcıda yalnızca bellekte.
- Bilinmeyen e-posta için de sahte bir şifre doğrulaması yapılıyor: "böyle bir kullanıcı yok" cevabı, "şifre yanlış"
  cevabıyla aynı sürede ve aynı mesajla dönüyor. Böylece hangi e-postanın kayıtlı olduğu tahmin edilemiyor.
- **Giriş denemesi sınırı:** IP başına dakikada 10 deneme, sayaç Redis'te. Redis kapalıysa sınır atlanıyor, giriş değil
  (bkz. 0005). Hesap kilitleme ayrıca koruyor.
- **Roller:** Yönetici, Muhasebeci, İzleyici. JWT'deki `role` alanından politika ile kontrol ediliyor. Varsayılan politika
  "giriş yapmış olmak": yeni bir uç nokta yanlışlıkla açık kalamaz.

## Sonuçları
- JWT 15 dakika geçerli ve geri alınamaz. Bir kullanıcının rolü değişirse yeni rol en geç 15 dakika sonra geçerli olur.
  Kabul ettiğimiz bir gecikme.
- Kilitli hesap önce 423 dönüyordu: kullanıcıya ne olduğunu söylemek, e-postanın kayıtlı olduğunu belli etmesine
  değer görülmüştü. Kod denetiminde (2026-11-22) karar değişti. Demo hesaplarının e-postaları herkese açık ve beş
  yanlış denemeyle kilitlenip sunumda kullanılamaz hale getirilebiliyordu; 423 ayrıca hangi e-postanın kayıtlı olduğunu
  söylüyordu. Artık kilitli hesap da yanlış şifreyle aynı cevabı, aynı sürede veriyor (401). Bedeli: kullanıcı kilitli
  olduğunu ekrandan anlamıyor; bunu belgeler ve 15 dakikalık süre söylüyor.
- İmzalama anahtarı yapılandırmada. Gerçek bir kurulumda bir sır kasasında olmalı ve döndürülmeli.

## Mülakatta
"Şifre ve kilitleme Identity'de, token'lar bende. Erişim anahtarı 15 dakikalık, yenileme anahtarı httpOnly çerezde ve
tek kullanımlık. Kullanılmış biri tekrar gelirse oturumun tamamını iptal ediyorum, çünkü bu birinin anahtarı kopyaladığı
anlamına gelir."
