# Bir kuruşun bankadan ERP'ye yolculuğu: tek fiş garantisi

*Taslak 1. Medium'a yapıştırmadan önce [Görsel] yazan yerlere resimleri koy.*

---

Bir şirketin muhasebe ekibinin sabahı çoğu zaman aynı başlar: bankaların ekstrelerini indir, hangi paranın hangi müşteriden
geldiğini bul, muhasebe programına tahsilat fişi olarak gir. Üç banka, günde yüz hareket, ayda birkaç bin satır.

Bunu otomatikleştiren bir sistem yazmak ilk bakışta kolay görünüyor: bankadan hareketleri çek, cariyle eşleştir, ERP'ye
gönder. Zor olan "mutlu yol" değil. Zor olan şu soruların cevabı:

- Aynı hareket bankadan iki kez gelirse ne olur?
- İki sunucu aynı hesabı aynı anda çekerse?
- ERP fişi yazar ama cevap vermeden bağlantı koparsa, tekrar gönderince ikinci bir fiş mi oluşur?
- ERP bütün gün kapalıysa iş kaybolur mu?

Para söz konusu olduğunda bu soruların cevabı "genelde olmaz" olamaz. Bu yazıda, bu sorular için kurduğum **Kasa** adlı
pratik projede hangi kararları neden verdiğimi anlatıyorum. Kod .NET 10, SQL Server, Redis ve RabbitMQ ile yazıldı;
bankalar ve ERP test için kurgulanmış sunucular.

[Görsel 1: Kasa'nın mimarisi. Web, Kasa.Api (SQL Server, Redis, MongoDB), RabbitMQ, ERP aktarım servisi, test ERP'si.]

## Önce soruları yazdım

Kodun ilk satırından önce bir tablo yazdım: "Bu çökerse ne olur?" Her satırda bir senaryo ve beklenen davranış vardı. Banka
kapalı, banka bozuk tutar gönderiyor, Redis kapalı, RabbitMQ kapalı, ERP fişi yazıp cevap vermiyor... Toplam 18 senaryo.
Sonra her biri için bir test yazdım.

Bu tablonun asıl faydası şu oldu: mimari kararları ezbere değil, bu sorulara cevap vererek verdim.

## Tek kayıt: kilit değil, veritabanı

Hareketler 15 dakikada bir otomatik çekiliyor, bir de "Şimdi çek" düğmesi var. Yani aynı hesap aynı anda iki yerden
çekilebilir: zamanlayıcı ile düğme, ya da birden fazla API sunucusunun zamanlayıcıları.

İlk akla gelen çözüm bir kilit. Kasa'da da Redis üzerinde bağlantı başına bir kilit var: `SET NX` ile 5 dakikalık bir
anahtar. Ama kilidin iki zayıf noktası var. Redis kapanırsa kilit yoktur. Bir senkron 5 dakikadan uzun sürerse kilidin
süresi dolar ve ikinci bir çalışma başlar.

Bu yüzden asıl garantiyi kilide değil, veritabanına verdim: `(BankAccountId, BankReference)` üzerinde benzersiz bir
indeks. Bankanın her harekete verdiği referans numarası, hesap başına tek olmak zorunda. Kilit sadece boşa iş yapılmasını
önlüyor; doğruluğu indeks sağlıyor.

Senkron önce elindeki referanslardan hangilerinin zaten kayıtlı olduğuna bakıyor, kalanları yazıyor. Arada başka bir
çalışma aynı hareketi yazdıysa indeks hata veriyor; senkron tekrar okuyup kalanı yazıyor:

```csharp
try
{
    await db.SaveChangesAsync(ct);
    return fresh.Count;
}
catch (DbUpdateException ex) when (attempt < 3 && IsUniqueViolation(ex))
{
    // Başka bir çalışma, bizim okumamızla yazmamız arasında bunların bir kısmını kaydetti.
    // Tekrar oku, kalanı kaydet.
    foreach (var entry in db.ChangeTracker.Entries()
                 .Where(e => e is { State: EntityState.Added, Entity: BankTransaction or MatchAudit or OutboxMessage }).ToList())
    {
        entry.State = EntityState.Detached;
    }
}
```

İki küçük ayrıntı önemli çıktı:

- **Referans karşılaştırması ikili (binary).** SQL Server'ın varsayılan karşılaştırması büyük/küçük harfe duyarsız. Bankanın
  "ab12" ve "AB12" referansları iki ayrı hareketse, duyarsız bir indeks ikincisini "zaten var" sayıp atlar ve para
  kaybolur. Sütunu `Latin1_General_100_BIN2` ile tanımladım.
- **Her senkron son iki günü yeniden okuyor.** Bankalar bazen bir hareketi bir iki gün geç işliyor. Zaten kayıtlı olanlar
  indeks sayesinde atlanıyor; yeniden okumanın bedeli sıfır.

Bunu nasıl kanıtladım? Redis'i bilerek ulaşılamaz bir adrese yönlendirip altı senkronu aynı anda başlatan bir test var. Kilit
yok, altısı da yarışıyor; sonunda her hareket tam bir kez kayıtlı.

## Eşleşme ile mesaj aynı anda: outbox

Hareket bir cariyle eşleşince ERP'ye "şu cariye şu tutarda tahsilat fişi yaz" mesajı gitmeli. ERP yavaş ve kapanabildiği için
bu işi ana uygulama değil, RabbitMQ üzerinden ayrı bir servis yapıyor.

Burada klasik bir tuzak var: eşleşmeyi veritabanına yazmak ve mesajı RabbitMQ'ya göndermek iki ayrı sistem, iki ayrı işlem.
Kayıttan sonra uygulama çökerse mesaj hiç gitmez; hareket "eşleşti" görünür ama ERP'de fiş yoktur. Önce mesajı gönderip sonra
kayıt geri alınırsa, ortada sahipsiz bir fiş kalır.

Çözüm **outbox** deseni: mesajı da aynı veritabanı işleminde bir tabloya yazıyorum. Eşleşme, denetim kaydı ve outbox satırı
tek bir `SaveChanges` çağrısında yazılıyor; ya üçü birden var ya hiçbiri.

Ayrı bir döngü saniyede bir bu tabloyu okuyup RabbitMQ'ya aktarıyor. Birden fazla API sunucusu aynı satırı almasın diye satırlar
şöyle seçiliyor:

```sql
SELECT TOP (50) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE PublishedAt IS NULL ORDER BY CreatedAt
```

`UPDLOCK` okuduğum satırları işlem bitene kadar kilitliyor, `READPAST` başkasının kilitlediği satırları beklemeden atlıyor.
Sunucular işi paylaşıyor, birbirini beklemiyor.

Bir satır, RabbitMQ yayını onayladıktan (publisher confirms) sonra "gönderildi" işaretleniyor. Yayından sonra, işaretlemeden
önce çökersek mesaj tekrar gider. Yani teslimat "en az bir kez". Bu bilinçli bir tercih: mesajı kaybetmektense iki kez
göndermeyi seçiyorum. Çünkü iki kez gelen mesajı zararsız hâle getirmenin bir yolu var.

## İki kez gelen mesaj, tek fiş: idempotency

Her ERP mesajının sabit bir kimliği var (`ExportId`) ve ERP'ye `Idempotency-Key` başlığı olarak gidiyor. ERP aynı anahtarı
ikinci kez görünce yeni fiş yazmıyor, ilk yazdığı fişi döndürüyor. Aynı anahtarla farklı bir içerik gelirse de reddediyor,
çünkü bu bir istemci hatasıdır.

```csharp
// Bu mesaj her geldiğinde aynı kimlik: ERP kaç kez sorarsak soralım tek fiş yazar.
request.Headers.Add("Idempotency-Key", message.ExportId.ToString());
```

En sinsi senaryo şu: ERP fişi yazıyor ama cevap vermeden bağlantı kopuyor. Aktarım servisi "olmadı" sanıp tekrar
gönderiyor. Bunu test etmek için test ERP'sine bir "cevabı kaybet" modu ekledim: fişi yazıyor, sonra 30 saniye
susuyor. Testte aktarım servisi 2 saniye sonra zaman aşımına düşüyor ve tekrar deniyor. ERP aynı anahtarı tanıyıp ilk fişi döndürüyor.
Test, ERP'deki fiş sayısının eşleşen hareket sayısına eşit kaldığını kontrol ediyor.

Bir de mesajı ne zaman onayladığım (ack) önemli. Sonucu Kasa'ya bildiren mesajı yayınladıktan **sonra** ack'liyorum. Arada
çökersem mesaj tekrar gelir, ERP aynı fişi döner, sonuç tekrar yayınlanır. Tekrar zararsız; kayıp değil.

## Hata türlerini ayırmak

Her hata aynı değil, aynı şekilde ele alınmamalı:

- **Geçici hata:** ERP kapalı, 5xx, zaman aşımı. Tekrar denemek işe yarayabilir. Mesaj 10 saniye bekleyen bir kuyruğa
  (`erp.export.retry`) gidiyor; süre dolunca RabbitMQ onu ana kuyruğa geri koyuyor. Deneme sayısı mesaj başlığında.
- **Kalıcı hata:** ERP fişi reddetti, örneğin cari kart ERP'de silinmiş (422). Kaç kez denesen aynı cevap. Tekrar denenmiyor.
- **Deneme hakkı biten ya da kalıcı hata alan mesaj** ölü mektup kuyruğuna (`erp.export.dead`) gidiyor ve hareket ekranda
  "Aktarılamadı" olarak görünüyor. Kullanıcı düzeltip yeniden gönderebiliyor; bu yeni bir `ExportId`, yani yeni bir istek.

Neden mesajı `nack` ile anında kuyruğa geri koymadım? Çünkü ERP kapalıyken bu, saniyede yüzlerce deneme yapan sıcak bir döngü
olur. Bekleme kuyruğu hem ERP'yi hem servisi rahatlatıyor.

[Görsel 2: Kuyruk yapısı. kasa.erp exchange; erp.export, erp.export.retry (TTL 10 sn), erp.export.dead, erp.results.]

## Biri kapanırsa

Yukarıdaki kararların toplamı şu tablo. Her satırın bir testi var:

| Kapanan | Ne oluyor |
|---|---|
| Banka | Senkron "başarısız" olarak kaydediliyor, bağlantı etkin kalıyor, sonraki senkron telafi ediyor |
| Redis | Kilitsiz devam ediliyor; çift kaydı veritabanı engelliyor. Panel önbelleksiz hesaplanıyor |
| RabbitMQ | Outbox satırları veritabanında bekliyor; RabbitMQ'ya ulaşılınca gönderiliyor |
| ERP | Mesajlar bekleme kuyruğunda dönüyor; ERP açılınca aktarılıyor |
| Aktarım servisi | Mesajlar kuyrukta bekliyor; servis açılınca işliyor |

Ölçüm de yaptım. Tek şirkette 500 bin hareketle:

- Hareketler listesinin ilk sayfası 4,9 ms.
- 250 bininci satırdan sonraki sayfa da 6,1 ms. Bunu OFFSET yerine "son gördüğümden sonrakiler" diyen keyset sayfalama
  sağlıyor. Aynı sayfa düz SQL'de OFFSET ile 55 ms, keyset ile 1,8 ms sürüyor.
- Panel önbelleksiz 253 ms, Redis'teki önbellekten 1,2 ms. Önbellek, bir şey değişince sürüm numarası artırılarak geçersiz
  kılınıyor; böylece eski veri gösterme riski yok.

## Bilerek bıraktığım eksikler

Bir sistemi anlatırken eksiklerini de bilmek gerekiyor:

- Gönderilmiş outbox satırları silinmiyor; tablo büyüdükçe bir temizlik işi gerekecek.
- Banka çağrılarında otomatik yeniden deneme yok; 15 dakika sonraki senkron telafi ediyor.
- Yeniden deneme bekleme süresi sabit. Artan bekleme (exponential backoff) birden fazla bekleme kuyruğu ister; şimdilik gerekmedi.

## Sonuç

Bu projeden çıkardığım en önemli ders şu: para tutan bir sistemde doğruluğu uygulama koduna değil, veritabanı kurallarına
ve idempotency'ye bağlamak gerekiyor. Kilitler, önbellekler ve kuyruklar sistemi hızlandırıyor, yükü dağıtıyor; ama "bu
hareket tek kez kaydedildi mi, bu fiş tek kez yazıldı mı" sorusunun cevabı her biri çökse bile değişmemeli.

Kodun tamamı, karar notları ve 18 hata senaryosunun testleri GitHub'da: [bağlantı]

---

*Görseller için not: Görsel 1 README'deki mimari diyagramdan, Görsel 2 kuyruk yapısından çizilecek. Kapak için Kasa'nın
giriş ekranındaki ekstre görüntüsü kullanılabilir.*
