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
| Giriş denemesi sınırı | Sınır atlanır | Hesap başına kilitleme (Identity, SQL'de) ayrıca koruyor |
| Senkron kilidi | Kilitsiz devam edilir | Çift kaydı asıl engelleyen, veritabanındaki benzersizlik kuralı. Kilit sadece boşa iş yapılmasını önlüyor |
| Panel önbelleği | Doğrudan veritabanından hesaplanır | Sadece daha yavaş |

- Bağlantı uygulamanın açılmasını beklemiyor (`AbortOnConnectFail = false`). Komutlar 1 saniyede vazgeçiyor.
- Bellek politikası `volatile-lru`: bellek dolunca yalnızca süresi olan anahtarlar siliniyor. Bütün anahtarlarımızın
  süresi var. Böylece bir kilit süresinden önce silinmez.
- Kalıcılık kapalı. Redis'teki hiçbir şeyin yeniden başlatmada korunması gerekmiyor.

## Sonuçları
- Redis kapalıyken giriş sınırı yok. Bu süre logda uyarıyla görünüyor.
- "Redis kapalı" durumu testli: Redis'e ulaşılamazken giriş çalışıyor.

## Mülakatta
"Redis'i hızlandırıcı olarak kullandım. Her kullanım için 'Redis yoksa ne olur?' sorusunun cevabını yazdım. Doğruluğu
Redis'e değil veritabanına bağladım: çift kaydı benzersizlik kuralı, şifre denemesini hesap kilidi engelliyor."
