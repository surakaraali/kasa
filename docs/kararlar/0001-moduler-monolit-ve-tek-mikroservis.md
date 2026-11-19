# 0001: Modüler monolit, ERP aktarımı için tek mikroservis

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Kasa'da birçok iş var: kimlik, banka bağlantıları, hareket senkronu, eşleştirme, panel, yapay zekâ asistanı ve ERP
aktarımı. Bunları tek bir uygulamada mı, yoksa ayrı servislerde mi çalıştırmalıyız?

Kısıtlar: tek geliştirici, bir haftalık süre, 8 GB RAM'li bir dizüstü. İlan "tercihen mikroservis mimarileri hakkında
bilgi" istiyor.

## Seçenekler
1. **Her modül ayrı mikroservis.** Bağımsız yayına alma ve ölçekleme. Ama her servisin kendi veritabanı, ağ çağrıları,
   dağıtık işlem sorunları ve izleme yükü var. Küçük bir ekip için karmaşıklık, getirdiği faydadan büyük.
2. **Tek parça uygulama (monolit).** Basit, hızlı. Ama yavaş ve sık çöken ERP'ye yapılan çağrılar ana uygulamayı bekletir;
   ERP kapalıyken iş kaybolmasın diye ayrıca bir mekanizma gerekir.
3. **Modüler monolit + ERP aktarımı ayrı servis.** Ana uygulama tek parça, içi net sınırlarla modüllere ayrılmış.
   Sadece ayrı olması gerçekten fayda getiren parça ayrı.

## Karar
**3. seçenek.** `Kasa.Api` modüler monolit. `Kasa.ErpExporter` ayrı servis; ana uygulamayla yalnızca RabbitMQ üzerinden,
Outbox deseniyle konuşuyor.

ERP aktarımının ayrılma sebepleri:
- ERP yavaş ve zaman zaman kapalı. Ana uygulama onu beklememeli.
- ERP kapalıyken iş kaybolmamalı. Kuyrukta bekleyip ERP açılınca devam etmeli.
- Aktarımın yükü ve hataları kullanıcı isteklerinden bağımsız olmalı. Gerekirse ayrıca ölçeklenebilmeli.

## Sonuçları
- Servisler arası tek sözleşme RabbitMQ mesajı. Mesaj en az bir kez teslim edileceği için tüketici idempotent olmak zorunda.
- Ana uygulamanın modülleri birbirinin tablolarına doğrudan dokunmuyor. İleride bir modülü ayırmak gerekirse sınırı hazır.
- Bir modülün ağır yükü (ör. senkron) diğerlerini etkileyebilir. Ölçüm bunu gösterirse o modül de ayrılır.

## Mülakatta
"Her şeyi mikroservis yapmadım; ayrı olması gerçekten fayda getiren tek parçayı, yavaş ve güvenilmez ERP'ye yazan kısmı
ayırdım. Gerisi tek uygulamada ama net modül sınırlarıyla; gerekirse sınır hazır."
