# 0010: Cari eşleştirme kuralları ve iyimser eşzamanlılık

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Banka hareketinde karşı tarafın adı, çoğu zaman IBAN'ı, bazen vergi numarası var. ERP'de cari kartın kodu, unvanı ve
bazen IBAN'ı ile vergi numarası var. Hangi hareket hangi cariye yazılacak? Yanlış cariye yazılan para, henüz yazılmamış
paradan daha kötü: müşteriye yanlış ekstre gider, mutabakat bozulur. Ayrıca bir muhasebeci elle eşlerken başka biri aynı
hareketi değiştirebilir.

## Seçenekler
1. **Bulanık eşleştirme (unvan benzerliği, tutar, açıklama).** Daha çok hareket eşleşir, ama yanlış eşleşme riski yüksek
   ve kural açıklanamaz.
2. **Kesin kurallar, şüphede kişiye bırak.** Daha az otomatik eşleşme, ama her eşleşmenin nedeni belli.

## Karar
**2. seçenek.**
1. IBAN tam olarak tek bir cariye uyuyorsa o cari.
2. Olmazsa vergi numarası tek bir cariye uyuyorsa o cari.
3. IBAN bir cariyi, vergi numarası başka bir cariyi gösteriyorsa ya da iki cari aynı değere sahipse eşleşme yok; kişi karar verir.

IBAN ve vergi numarası iki tarafta da aynı biçimde saklanır (boşluksuz, büyük harf; 10 ya da 11 hane), karşılaştırma birebir.
Kurallar hem hareket kaydedilirken hem cari kartlar alındığında çalışır. Adaylar tek sorguda yüklenir, hareket başına sorgu yok.

Elle eşleme `PUT /api/transactions/{id}/match` ile yapılır. İstek, kişinin gördüğü `rowVersion`'ı taşır. SQL Server
`rowversion` sütunu her değişiklikte artar; EF Core güncellemeyi `WHERE RowVersion = @gördüğün` ile yapar. Arada biri
değiştirdiyse 0 satır güncellenir ve kişi 409 alır: "siz bakarken değişti". Kilit tutulmaz (iyimser eşzamanlılık).
"ERP'ye aktarılmayacak" (örneğin banka masrafı) da bir karar: cari boş gönderilir.

Her karar (kural ya da kişi) `MatchAudits` tablosuna yazılır: kim, ne zaman, hangi cari, hangi kural. Bu tablo eşleşmeyle
aynı işlemde. MongoDB'ye değil SQL'e yazıldı, çünkü denetim kaydı kaybolmayı göze alamaz.

## Sonuçları
- Test verisinde kuralların hepsi görünüyor: IBAN'la, vergi numarasıyla, çakışma, hiç ipucu yok.
- Banka masrafları ve ERP'de kartı olmayan karşı taraflar "eşleşmeyenler" ekranına düşüyor.
- Kurallar saf bir fonksiyon (`Matching.Decide`), veritabanı olmadan birim testli.

## Mülakatta
"Eşleştirmede doğruluğu kapsama tercih ettim: bir kural ancak tek bir cari kalıyorsa karar veriyor, iki ipucu çelişirse kişiye
bırakıyor. Elle eşlemede rowversion ile iyimser eşzamanlılık kullandım; iki kişi aynı hareketi değiştirirse ikincisi 409 alıyor,
kimse kimsenin kararını sessizce ezmiyor."
