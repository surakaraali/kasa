# Kasa

Şirketler için nakit yönetimi: bütün banka hesapları tek ekranda, hareketler otomatik olarak doğru cari hesapla
eşleşiyor, muhasebe programına (Logo Tiger benzeri bir test ERP'si) aktarılıyor ve sorular bir yapay zekâ asistanına
sorulabiliyor.

> Bu bir pratik projesi. Bankalar ve ERP test için kurgulandı, bütün veriler örnek veridir.

**Durum:** Sprint 2 tamamlandı (kimlik, şirket izolasyonu, iki banka entegrasyonu ve senkron). Plan ve sprintler: [docs/PLAN.md](docs/PLAN.md)

## Mimari

```mermaid
flowchart LR
    web["Web arayüzü"] --> api["Kasa.Api<br/>modüler monolit"]
    api --> sql[("SQL Server")]
    api --> redis[("Redis")]
    api --> mongo[("MongoDB")]
    api -- "Outbox → RabbitMQ" --> erp["Kasa.ErpExporter"]
    erp --> tiger["Kasa.MockErp"]
    api --> banks["Kasa.MockBanks"]
```

Neden böyle: [docs/kararlar/0001](docs/kararlar/0001-moduler-monolit-ve-tek-mikroservis.md)

| Proje | Ne | Adres |
|---|---|---|
| `src/Kasa.Api` | Ana uygulama | http://localhost:5100 |
| `src/Kasa.ErpExporter` | ERP aktarım servisi | http://localhost:5101 |
| `src/Kasa.MockBanks` | Test bankaları (REST ve SOAP) | http://localhost:5102 |
| `src/Kasa.MockErp` | Test ERP'si | http://localhost:5103 |
| `tests/Kasa.IntegrationTests` | Entegrasyon testleri | |

## Çalıştırma

Gerekenler: .NET 10 SDK ve Docker.

```bash
cp .env.example .env
docker compose up -d --wait        # SQL Server, Redis, MongoDB, RabbitMQ
dotnet run --project src/Kasa.Api  # her servis için ayrı bir terminalde
dotnet test
```

| Altyapı | Adres |
|---|---|
| SQL Server | `localhost,14335` |
| Redis | `localhost:6379` |
| MongoDB | `localhost:27017` |
| RabbitMQ | `localhost:5672`, yönetim paneli http://localhost:15672 |

Bankaları denemek için `src/Kasa.MockBanks` ve `src/Kasa.Api` çalışırken Swagger'dan (http://localhost:5100/swagger) kayıt olun,
sonra `POST /api/bank-connections/klasikbank` ile bağlanın (kullanıcı `kasa`, şifre `klasik-dev-pass`; test bankasının
sabit değerleri). Açık bankacılık için `POST /api/bank-connections/acikbank` dönen adresi tarayıcıda açın.

Bellek sınırları 8 GB RAM'li bir dizüstüne göre ayarlı; dört servis toplam yaklaşık 4 GB ile sınırlı.

## Kararlar

Her teknoloji seçiminin gerekçesi `docs/kararlar/` altında: bağlam, seçenekler, karar ve sonuçları.

- [0001: Modüler monolit, ERP aktarımı için tek mikroservis](docs/kararlar/0001-moduler-monolit-ve-tek-mikroservis.md)
- [0002: .NET 10 (LTS) ve tek yerden sabitlenen paket sürümleri](docs/kararlar/0002-dotnet-10-ve-sabit-surumler.md)
- [0003: Kimlik: Identity, kısa ömürlü JWT, rotasyonlu yenileme anahtarı](docs/kararlar/0003-kimlik-jwt-ve-yenileme-anahtari.md)
- [0004: Şirket izolasyonu: sorgu filtresi ve yazma koruması](docs/kararlar/0004-sirket-izolasyonu.md)
- [0005: Redis bir hızlandırıcı, zorunluluk değil](docs/kararlar/0005-redis-bir-hizlandirici.md)
- [0006: Banka bağlantısı: açık bankacılık akışı ve sırların saklanması](docs/kararlar/0006-banka-baglantisi-ve-sirlar.md)
- [0007: Banka senkronu: tek kayıt garantisi, kilit, zamanlama ve hatalar](docs/kararlar/0007-senkron-tek-kayit-ve-hatalar.md)
- [0008: Ham banka cevaplarının MongoDB'de arşivlenmesi](docs/kararlar/0008-ham-cevap-arsivi-mongodb.md)

Sprint sonu soruları ve cevap ipuçları: [docs/sorular.md](docs/sorular.md). İş takibi: [GitHub Projects panosu](https://github.com/users/surakaraali/projects/1).
