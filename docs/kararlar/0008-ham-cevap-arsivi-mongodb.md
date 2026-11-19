# 0008: Ham banka cevaplarının MongoDB'de arşivlenmesi

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
"Banka o gün tam olarak ne gönderdi?" sorusu muhasebede sık çıkar: bir tutar yanlış görünür, bir hareket eksik görünür.
Ayrıştırılmış satırlar bu soruya cevap vermez; bankanın gönderdiği ham cevap gerekir. Cevaplar iki biçimde (JSON ve SOAP
XML), her biri farklı şekilde, bir kez yazılıp nadiren okunuyor ve bir süre sonra gereksiz.

## Seçenekler
1. **SQL Server'da bir tablo.** Tek veritabanı. Ama büyük metinler ana veritabanını şişirir, yedekleri büyütür; eski
   kayıtları silmek için ayrıca bir iş gerekir.
2. **Dosya ya da nesne deposu (S3, Blob).** Ucuz. Ama "bu bağlantının dünkü cevapları" gibi bir sorgu için ayrıca indeks gerekir.
3. **MongoDB.** Belge biçimi şekilden bağımsız, bağlantıya ve zamana göre indeks var, TTL indeksiyle eski belgeleri kendisi siliyor.

## Karar
**3. seçenek.** Bankaya giden HttpClient'larda bir `DelegatingHandler` var. Sadece üzerinde etiket (`RawResponseTag`) olan
istekleri arşivliyor: hesap ve hareket çağrıları. Anahtar çağrıları etiketsiz, çünkü cevaplarında anahtar var.
- Saklanan: şirket, bağlantı, banka, işlem, yol ve sorgu, durum kodu, gövde (256 KB'a kadar), zaman.
- Saklanmayan: istek başlıkları (Bearer anahtarı orada) ve SOAP isteğinin gövdesi (şifre orada). Sadece cevap saklanıyor.
- `receivedAt` üzerinde TTL indeksi (90 gün) ve `(connectionId, receivedAt)` indeksi.
- MongoDB de Redis gibi isteğe bağlı: ulaşılamazsa uyarı loglanır, senkron devam eder. Sunucu seçme süresi 1 saniye
  (sürücünün varsayılanı 30 saniye).

## Sonuçları
- Arşiv testli: SOAP ve REST cevapları yazılıyor, hiçbir belgede şifre ya da `access_token` yok, TTL indeksi var.
- MongoDB kapalıyken senkron çalışıyor (testli). Bu sırada her banka çağrısı en fazla 1 saniye gecikiyor.
- shortcut: arşiv yazımı banka çağrısının yolunda. Yük artarsa sınırlı bir kuyruğa (Channel) alınıp arka planda yazılmalı.

## Mülakatta
"MongoDB'yi asıl veri için değil, şekli belirsiz, bir kez yazılan ve kendiliğinden silinmesi gereken ham cevaplar için
kullandım. Arşive neyin girmediğine de dikkat ettim: anahtar çağrıları ve istek başlıkları hiç yazılmıyor."
