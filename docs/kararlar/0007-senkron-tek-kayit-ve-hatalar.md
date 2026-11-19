# 0007: Banka senkronu: tek kayıt garantisi, kilit, zamanlama ve hatalar

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Hareketler 15 dakikada bir ve "şimdi çek" düğmesiyle çekiliyor. Aynı bağlantı aynı anda iki yerden çekilebilir: iki API
örneğinin zamanlayıcısı, zamanlayıcı ile düğme, art arda iki tıklama. Bankalar da kusursuz değil: kapanıyor, yavaşlıyor,
bozuk veri yolluyor, bir hareketi bir iki gün geç işliyor. Bir hareket iki kez kaydedilirse kasa raporu yanlış olur.

## Seçenekler
1. **Sadece kilit.** Redis kilidi aynı anda tek çalışmaya izin verir. Ama Redis kapanırsa ya da kilit süresi dolarsa çift
   kayıt mümkün.
2. **Sadece veritabanı kuralı.** `(BankAccountId, BankReference)` benzersiz. Çift kayıt imkânsız, ama yarışan her çalışma
   bankayı boşuna sorgular.
3. **İkisi birden.** Kilit boşa işi önlüyor; doğruluğu veritabanı garanti ediyor.

## Karar
**3. seçenek.**
- **Doğruluk:** `(BankAccountId, BankReference)` benzersiz indeks, ikili karşılaştırmayla (`Latin1_General_100_BIN2`):
  "abc" ile "ABC" iki ayrı referans. Senkron önce elindeki referanslardan hangilerinin zaten kayıtlı olduğuna bakar, kalanları
  yazar. Arada başka bir çalışma aynı hareketi yazdıysa indeks hata verir (2601/2627); senkron tekrar okur ve kalanı yazar.
- **Boşa iş:** Redis'te bağlantı başına kilit (`SET NX`, 5 dakika süreli). Bırakırken Lua betiğiyle "hâlâ benimse sil";
  süresi dolup başkasına geçmiş bir kilit silinmez. Redis kapalıysa kilitsiz devam edilir (karar 0005).
- **Geç işlenen hareketler:** Her senkron son iki günü yeniden okur. Zaten kayıtlı olanlar atlanır. İlk senkron 30 gün geriye gider.
- **Zamanlama:** Quartz, bellekte, Türkiye saatine göre cron (`0 0/15 * * * ?`). `[DisallowConcurrentExecution]` aynı
  sunucuda iş üst üste binmesin diye; sunucular arası çakışmayı kilit çözüyor. Kalıcı (veritabanlı) Quartz deposu gerekmedi:
  kaçan bir çalışma 15 dakika sonra telafi ediliyor.

Hatalar üç türe ayrılıyor, her birinin sonucu tanımlı:

| Tür | Örnek | Sonuç |
|---|---|---|
| Ulaşılamıyor | 503, zaman aşımı (10 sn), bağlantı hatası | Senkron "başarısız", hata kaydedilir, bağlantı etkin kalır, sonraki çalışma telafi eder |
| Reddedildi | Rıza iptal, şifre değişmiş | Bağlantı "yeniden bağlanmalı" olur, zamanlayıcı artık denemez |
| Bozuk veri | "12,5O" tutarı, SOAP'ta "1250.50" | O hesabın cevabının tamamı reddedilir, hiçbir satırı kaydedilmez |

Erişim anahtarı süresi dolduysa (401) bir kez yenilenip tekrar deneniyor. Tutarlar sadece katı kalıba uyuyorsa okunuyor;
"1250.50"yi Türkçe biçimde gevşek okumak 125050 TL yapardı.

## Sonuçları
- Redis kapalıyken altı eşzamanlı senkron her hareketi bir kez yazıyor (testli). Redis açıkken biri çalışıyor, diğerleri
  409 "zaten çekiliyor" alıyor (testli).
- Bozuk bir hareket o hesabın bütün cevabını durduruyor. Bilerek: yarım bir ekstre, eksiksiz görünen yanlış bir ekstredir.
- Bankaya otomatik yeniden deneme (Polly) eklenmedi; bir sonraki çalışma zaten yeniden deneme. Kullanıcı "şimdi çek"e
  basıp hata alırsa sebebini görüyor.

## Mülakatta
"Çift kaydı kilide değil veritabanına bağladım. Kilit sadece boşa iş yapılmasını önlüyor; Redis'i kapatıp altı senkronu aynı
anda çalıştıran bir testim var, her hareket bir kez yazılıyor. Banka hatalarını üçe ayırdım: geçici olan tekrar denenir,
kalıcı olan kullanıcıdan yeniden bağlanmasını ister, bozuk veri hiç kaydedilmez."
