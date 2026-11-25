# 0005: Redis bir hızlandırıcı, zorunluluk değil

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Redis'i üç iş için kullanıyoruz: panel toplamlarının önbelleği, aynı banka bağlantısının aynı anda iki kez çekilmemesi
için kilit, giriş denemesi sınırı. Redis çökerse ne olmalı?

## Seçenekler
1. **Redis yoksa ilgili özellik hata versin (fail-closed).** En katı. Ama önbellek çöktü diye giriş sayfası ya da panel
   çalışmaz hâle gelir.
2. **Redis yoksa o adım atlanıp devam edilsin (fail-open).** Sistem yavaşlar ama çalışır. Ancak her özellik için ayrı ayrı
   "atlanırsa ne kaybederiz?" sorusunun cevabı olmalı.

## Karar
**2. seçenek, her kullanım için gerekçesiyle:**

| Kullanım | Redis yoksa | Neden güvenli |
|---|---|---|
| Deneme sınırları (giriş, kayıt kodu, mevcut şifre, asistan) | Aynı sınır bu süreçte sayılır | Sınır Redis'le birlikte kaybolmuyor, giriş de çalışmaya devam ediyor |
| Senkron kilidi | Kilitsiz devam edilir | Çift kaydı asıl engelleyen, veritabanındaki benzersizlik kuralı. Kilit sadece boşa iş yapılmasını önlüyor |
| Panel önbelleği | Doğrudan veritabanından hesaplanır | Sadece daha yavaş |

- Bağlantı uygulamanın açılmasını beklemiyor (`AbortOnConnectFail = false`). Komutlar 1 saniyede vazgeçiyor.
- Bellek politikası `volatile-lru`: bellek dolunca süresi olan anahtarlar siliniyor. Bütün anahtarlarımızın süresi var,
  yani bir kilit de erken silinebilir. O zaman ikinci bir senkron boşa çalışabilir; çift kaydı yine indeks engeller.
- Kalıcılık kapalı. Redis'teki hiçbir şeyin yeniden başlatmada korunması gerekmiyor.

## Sonuçları
- Redis kapalıyken sınırlar süreç içinde sayılıyor ve logda uyarı çıkıyor. Birden çok API örneğinde gerçek sınır örnek
  sayısıyla çarpılır (kodda `shortcut:` notu var).
- İlk hâlinde sınır Redis yokken tamamen atlanıyordu. Kod incelemesinde (2026-11-23) bunun kayıt kodunu, mevcut şifreyi ve
  asistanın saatlik sınırını korumasız bıraktığı görüldü, çünkü hesap kilidi bu üçünü kapsamıyor. Süreç içi sayaca geçildi.
- "Redis kapalı" durumu testli: Redis'e ulaşılamazken giriş çalışıyor ve deneme sınırı sayılmaya devam ediyor.

## Mülakatta
"Redis'i hızlandırıcı olarak kullandım. Her kullanım için 'Redis yoksa ne olur?' sorusunun cevabını yazdım. Doğruluğu
Redis'e değil veritabanına bağladım: çift kaydı benzersizlik kuralı engelliyor, panelin sürümü SQL'de. Redis kapalıyken deneme
sınırlarını süreç içinde sayıyorum."
