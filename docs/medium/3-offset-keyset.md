# 5.000. sayfa neden yavaştı: OFFSET ve keyset sayfalama

Kasa adlı pratik projem, şirketlerin banka hareketlerini tek ekranda listeliyor. Hareketler iki test bankasından geliyor.
Listenin büyük veride nasıl davrandığını görmek için tek bir şirkete 500.000 hareket yükledim. Satırlar üç hesaba ve iki
yıla yayılmıştı.

Sonra şunu ölçtüm: Sayfada 50 satır varken 250.000 satırı geçip 5.000. sayfanın ardına inen bir kullanıcı ne kadar bekler?
Aynı sayfayı iki yöntemle, yalnız SQL tarafında çalıştırdım.

| Yöntem | Ortanca süre |
|---|---|
| OFFSET | 46,9 ms |
| Keyset | 1,3 ms |

Fark yaklaşık 36 kat. 47 ms tek başına kötü bir süre sayılmaz. Beni ilgilendiren, bu sürenin neye bağlı olduğuydu.

## OFFSET atladığı satırları da okuyor

`OFFSET 250000 ROWS` yazdığımda SQL Server hedef satırın yerini bilmiyor. Sıralı indeksi baştan yürüyor, 250.000 satırı sayıp
atıyor, sonraki 50 satırı veriyor. İşin büyük kısmı kullanıcının hiç görmeyeceği satırlar için yapılıyor. Sayfa ilerledikçe
atlanacak satır sayısı da artıyor.

İkinci sorun kayma. Kullanıcı sayfalar arasında gezerken yeni bir hareket gelirse bütün satırlar bir sıra aşağı iniyor.
Önceki sayfanın son satırı sonraki sayfanın başında tekrar görünüyor.

## Keyset kaldığı yerden devam ediyor

Keyset sayfalamada sayfa numarası yok. İstemci son gördüğü satırı söylüyor, sunucu ondan eskileri getiriyor. Liste en yeniden
en eskiye sıralı:

```csharp
rows = rows.Where(t => t.BookedAt < afterAt || (t.BookedAt == afterAt && t.Id < afterId));
// ...
var items = await rows.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Take(q.Limit + 1)
    // ...
    .ToListAsync(ct);
var next = items.Count > q.Limit ? Cursor(items[q.Limit - 1]) : null;
```

Koşulda `Id` bulunmasının nedeni eşit tarihler. İki hareket aynı anda işlenmiş olabilir. Yalnız tarihe baksaydım, sayfa
sınırına denk gelen satırlardan biri kaybolur ya da iki kez gelirdi. `Id` bu eşitliği bozuyor ve sırayı kesinleştiriyor.

`Take(q.Limit + 1)` de bilerek böyle. 50 satır istendiğinde 51 satır okuyorum. 51. satır varsa sonraki sayfa da var. Bunu
öğrenmek için ayrı bir `COUNT` sorgusu çalıştırmıyorum.

## İndeks ve imleç

Sorgunun hızı `(CompanyId, BookedAt, Id)` indeksine bağlı. Liste tek şirketin satırlarını okuyor, bu yüzden `CompanyId` başta
duruyor. Şirketin satırları onun içinde tarihe ve `Id`'ye göre sıralı. İndeks artan sırada tanımlı, liste azalan sırada
okuyor. SQL Server aynı indeksi geriye doğru okuyabildiği için ikinci bir indeks tanımlamadım. Veritabanı imlecin gösterdiği
noktaya gidiyor ve oradan 51 satır alıyor.

İmleç, son satırın tarihi ve `Id`'sinden oluşuyor:

```csharp
private static string Cursor(TransactionDto last) =>
    Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{last.BookedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{last.Id.ToString(CultureInfo.InvariantCulture)}"));
```

Tarihi tick olarak taşıyorum, böylece yuvarlama olmuyor. İmleci imzalamıyorum. İstemci onu elle değiştirirse en fazla kendi
şirketinin başka bir noktasına gider, çünkü sorgu yine şirket filtresinden geçiyor. Bozuk bir imleç 400 dönüyor.

`Paging_through_movements_returns_each_once_newest_first` testi hareketleri üçerli sayfalarla geziyor ve sonucun tek seferde
alınan listeyle aynı olduğunu doğruluyor. Bozuk imleci de aynı test deniyor.

Uçtan uca, yani HTTP ve JSON dahil, ilk sayfa 3,3 ms sürüyor. 250.000. satırın arkasındaki sayfa 3,4 ms. Bu ölçümler
`Half_a_million_movements` testinden geliyor ve `KASA_PERF=1` ile çalışıyor. Her değer 1 ısınma ve 7 çalıştırmanın ortancası.
Ortam yerel bir dizüstü, arada ağ yok.

## Keyset'in bedeli

Keyset bazı şeyleri elimden alıyor:

- Belirli bir sayfa numarasına atlanamıyor, toplam sayfa sayısı da yok. Ekranda yalnız "Daha eski hareketler" düğmesi var.
  Uzak bir tarihe gitmek için tarih filtresi kullanılıyor.
- İmleç sıralamaya bağlı. Tutara göre sıralama eklemek istersem yeni bir indeks ve yeni bir imleç biçimi gerekir.
- Bankalar bazen bir hareketi bir iki gün geç işliyor. Böyle bir satır imlecin geride bıraktığı tarihe düşerse o gezintide
  görünmez. Liste yeniden yüklenince görünür.
- Metin aramasını hızlandırmıyor. `LIKE '%...%'` sık geçen "ABC" için 4,6 ms sürdü, ama nadir bir kelimede şirketin bütün
  hareketlerini tarayabilir. Daha büyük veride tam metin araması gerekecek.

OFFSET'i de tek derinlikte ölçtüm. Derinlikle büyüdüğünü mekanizmadan biliyorum, farklı derinliklerde bir eğri çıkarmadım.
