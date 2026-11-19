# 0012: Panel önbelleği: sürüm anahtarıyla Redis

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Panel her açılışta bakiyeleri, son 30 günün günlük giriş/çıkışını, eşleşme ve aktarım sayılarını hesaplıyor. Bunlar şirketin
bütün hareketleri üzerinde toplama sorguları. Canlı güncellemeyle her değişiklikte açık olan her ekran paneli yeniden istiyor.
Hareket sayısı büyüdükçe bu pahalılaşır.

## Seçenekler
1. **Süreli önbellek (örneğin 5 dakika).** Basit, ama yeni bir hareket 5 dakika görünmez; canlı güncellemenin anlamı kalmaz.
2. **Değişince anahtarı silmek.** Silme ile yeniden yazma yarışabilir: eski sonucu hesaplayan bir istek, silmeden sonra eski
   veriyi tekrar yazabilir.
3. **Sürüm anahtarı.** Her şirketin bir sürüm sayacı var (`dashboard-version:{şirket}`). Önbellek anahtarı sürümü içerir
   (`dashboard:{şirket}:v{sürüm}:{gün}`). Değişiklik sayacı artırır; eski anahtarlar kendiliğinden süresi dolarak gider.

## Karar
**3. seçenek.** Senkron, eşleşme, cari alma ve ERP sonucu sürümü artırıyor (aynı yerde SignalR mesajı da gidiyor). Önbellek
10 dakika süreli. Redis kapalıysa panel her seferinde veritabanından hesaplanıyor (karar 0005). Cevapta `X-Cache: hit|miss`
ve `X-Compute-Ms` başlıkları var, ölçüm için.

Günlük gruplama SQL Server'da Türkiye saatine göre yapılıyor (`AT TIME ZONE 'Turkey Standard Time'`); banka hangi saat
diliminde gönderirse göndersin gün doğru.

## Sonuçları
- İlk ölçüm (yaklaşık 60 hareket, yerel makine): önbelleksiz 28 ms, önbellekten 2 ms. Asıl ölçüm 500 bin hareketle Sprint 6'da.
- Eski sürümlerin anahtarları 10 dakika daha bellekte kalıyor; `volatile-lru` gerekirse önce onları siliyor.
- Sayaç anahtarının da süresi var (7 gün); hepsinin süresi olması `volatile-lru` için şart.

## Mülakatta
"Önbelleği silmek yerine sürümledim: değişiklik sayacı artırıyor, anahtar sürümü içeriyor. Silme ile yeniden yazma arasındaki
yarış böylece ortadan kalkıyor ve 'ne zaman geçersiz kılarım' sorusu tek bir sayaca iniyor."
