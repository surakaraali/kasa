# 0014: İzleme: Serilog yerine OpenTelemetry ve Aspire paneli

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Bir hareketin yolu dört süreçten geçiyor: API (senkron, eşleşme, outbox), RabbitMQ, aktarım servisi, ERP. "Bu fiş neden
gecikti?" sorusunun cevabı tek bir süreçteki loglarda değil, bu yolun tamamında. Planda Serilog vardı.

## Seçenekler
1. **Serilog, dosyaya ya da Seq'e yapılandırılmış log.** İyi loglar, ama süreçler arası iz için ayrıca bir şey gerekir.
2. **OpenTelemetry: iz, metrik ve log tek standartla.** .NET'in kendi `ILogger`'ı OpenTelemetry'ye akar; ASP.NET Core,
   HttpClient, SqlClient ve RabbitMQ.Client 7 iz üretir. Yerelde Aspire paneli, üretimde herhangi bir OTLP alıcısı.

## Karar
**2. seçenek.** Dört servis ortak bir `Kasa.ServiceDefaults` projesi kullanıyor (.NET Aspire düzeni): OpenTelemetry ve iki
sağlık ucu. `/alive` sadece sürecin cevap verdiğini söylüyor; `/health` her bağımlılığı tek tek bildiriyor.
- İz kuyruk boyunca sürüyor: eşleşmeyi yapan isteğin izi outbox satırına (`TraceParent`) yazılıyor, yayıncı onu sürdürüyor,
  RabbitMQ.Client izi mesaj başlığında taşıyor, aktarım servisi ERP çağrısını aynı izin altında yapıyor.
- Outbox'un saniyede bir yaptığı boş sorgu izlenmiyor (`SuppressInstrumentationScope`); yoksa panel her saniye yeni bir izle dolardı.
- Dışa aktarma sadece `OTEL_EXPORTER_OTLP_ENDPOINT` varsa açık: testler ve çıplak `dotnet run` hiçbir yere bir şey göndermiyor.
  `launchSettings.json` bunu yerel panele yönlendiriyor.
- Sağlık: SQL Server yoksa "unhealthy"; Redis, MongoDB ve RabbitMQ yoksa "degraded" (200 dönmeye devam eder), çünkü üçü
  için de tanımlı bir devam yolu var. Arayüzdeki panel bu durumu gösteriyor.

## Sonuçları
- Bir banka bağlama isteği 4 servisi kapsayan tek bir iz olarak görünüyor (elle doğrulandı).
- Serilog'un zengin hedefleri yok; gerekirse Serilog OpenTelemetry'ye yazacak şekilde sonradan eklenebilir.
- Aspire paneli yerelde kimliksiz ve sadece 127.0.0.1'de.

## Mülakatta
"Planda Serilog vardı, OpenTelemetry'ye geçtim, çünkü sorun loglarda değil süreçler arasındaydı. Eşleşmeyi yapan isteğin
izini outbox satırına yazıp yayında devam ettirdim; böylece tıklamadan ERP fişine kadar tek bir iz var. Sağlık kontrollerinde
'degraded' ile 'unhealthy'yi ayırdım: Redis kapalıyken uygulama çalışıyor, bunu kapalı gibi göstermek yanlış alarm olur."
