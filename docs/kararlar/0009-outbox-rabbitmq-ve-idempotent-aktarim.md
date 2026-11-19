# 0009: ERP aktarımı: outbox, RabbitMQ ve idempotent tüketici

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Eşleşen her hareket ERP'de bir fişe dönüşmeli: para girdiyse tahsilat, çıktıysa tediye fişi. ERP yavaş olabilir,
kapanabilir, bir fişi reddedebilir. Ana uygulama ERP'yi beklememeli, ERP kapalıyken iş kaybolmamalı ve hiçbir hareket ERP'de
iki fiş olmamalı. Karar 0001 bu yüzden aktarımı ayrı bir servise (`Kasa.ErpExporter`) almıştı. Soru, işin oraya nasıl
güvenle taşınacağı.

## Seçenekler
1. **API eşleşmeyi kaydeder, sonra RabbitMQ'ya yayınlar.** Basit. Ama ikisi ayrı işlem: kayıttan sonra uygulama çökerse
   mesaj hiç gitmez; yayından sonra kayıt geri alınırsa ortada sahipsiz bir mesaj kalır (dual write).
2. **Dağıtık işlem (2PC).** RabbitMQ desteklemiyor.
3. **Outbox.** Mesaj, eşleşmeyle aynı veritabanı işleminde bir tabloya yazılır. Ayrı bir yayıncı tabloyu okuyup RabbitMQ'ya
   aktarır. "En az bir kez" teslim edilir; tekrarlanan mesajı tüketici idempotent olarak karşılar.

## Karar
**3. seçenek.**
- **Yazma:** Eşleşme, denetim kaydı ve `OutboxMessages` satırı tek `SaveChanges`, tek işlem. Ya üçü birden var ya hiçbiri.
- **Yayın:** `OutboxPublisher` saniyede bir en fazla 50 satır alır: `WITH (UPDLOCK, READPAST, ROWLOCK)`. Birden çok API örneği
  aynı satırı almaz; biri kilitlediğini öteki atlar. Satır, RabbitMQ yayını onayladıktan (publisher confirms) sonra
  "gönderildi" olur. Yayından sonra, işaretlemeden önce çökerse mesaj tekrar gider.
- **Tüketme:** `Kasa.ErpExporter` mesajları tek tek ve elle onaylayarak (manual ack) işler. ERP'ye `Idempotency-Key = ExportId`
  başlığıyla gider. Aynı anahtar ikinci kez gelirse ERP ilk fişi döner, yenisini yazmaz. Sonuç ack'ten önce yayınlanır;
  arada çökerse mesaj tekrar gelir ve ERP aynı fişi döner.
- **Hatalar:**
  - Geçici hata (ERP kapalı, 5xx, zaman aşımı): mesaj `erp.export.retry` kuyruğuna gider. Kuyruğun TTL'i dolunca
    (dead-letter) ana kuyruğa geri döner. Deneme sayısı başlıkta (`x-kasa-attempt`) tutulur.
  - Kalıcı hata (ERP fişi reddetti, örneğin cari kart silinmiş) ya da deneme hakkı bitti: mesaj `erp.export.dead`
    kuyruğuna gider ve Kasa'ya "Başarısız" sonucu yollanır. Kişi eşleşmeyi onaylayıp yeniden gönderebilir; bu yeni bir
    `ExportId`, yani yeni bir fiş isteği.
  - Okunamayan mesaj doğrudan ölü kuyruğa gider.
- **Sonuç:** `erp.results` kuyruğu. API hareketin durumunu "Aktarıldı" yapar ve fiş numarasını yazar. Hareketin şu anki
  `ExportId`'sine uymayan eski bir sonuç yok sayılır.
- Kuyruklar quorum tipi (çoğaltılmış, yeniden başlatmada kaybolmaz). İki uygulama da aynı topolojiyi tanımlar, biri
  ötekinden önce açılsa da mesaj kaybolmaz. Yayınlar `mandatory`: hiçbir kuyruğa gitmeyen mesaj sessizce kaybolmaz, hata verir.

## Sonuçları
- ERP kapalıyken iş bekliyor, açılınca akıyor; aktarım servisi kapalıyken mesajlar kuyrukta bekliyor (ikisi de testli).
- Outbox satırları yeniden gönderilse bile fiş sayısı değişmiyor (testli).
- Yeniden deneme bekleme süresi sabit (10 sn). Artan bekleme (exponential backoff) birden çok bekleme kuyruğu ister; gerekmedi.
- shortcut: gönderilmiş outbox satırları silinmiyor; tablo büyüdüğünde birkaç haftadan eskiler bir işle silinmeli.
- Aktarılmış bir hareketin eşleşmesi Kasa'da değiştirilemiyor (409). Düzeltme ERP'de, ters fişle yapılır.

## Mülakatta
"Veritabanına kaydedip mesaj yayınlamak iki ayrı işlem, arada çökerse ikisi tutarsız kalır. Outbox ile mesajı aynı işleme
koydum, yayını ayrı bir döngüye bıraktım. Bu 'en az bir kez' demek; o yüzden tüketiciyi idempotent yaptım: ERP'ye her mesajın
sabit bir Idempotency-Key'i gidiyor. Geçici hatayı bekleme kuyruğuyla tekrar deniyorum, kalıcı hatayı ölü kuyruğa atıp
kullanıcıya gösteriyorum."
