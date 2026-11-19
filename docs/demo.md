# Demo betiği (yaklaşık 10 dakika)

## Hazırlık (mülakattan 15 dakika önce)

```bash
scripts/demo.sh reset      # her şeyi durdurur, veritabanını boşaltır, üretim modunda yeniden başlatır (~30 sn)
```

Tarayıcıda dört sekme: Kasa http://localhost:3000, test ERP fişleri http://localhost:3000/test-erp, izleme paneli
http://localhost:18888, ikinci bir gizli pencerede Kasa (muhasebeci için). Bir terminal açık kalsın.

Test bankası giriş bilgileri: kullanıcı `kasa`, şifre `klasik-dev-pass` (test bankasının sabit değerleri).

## Hazır demo şirketi

Boş sistem yerine her şeyi bağlı bir şirketle başlamak için, `reset`'ten sonra:

```bash
scripts/demo-seed.sh       # genel adresle: KASA_PUBLIC_URL=https://kasa.surakaraali.dev scripts/demo-seed.sh
```

Anadolu Tekstil A.Ş.: ERP firması 001 ve 7 cari kart, iki banka, yaklaşık 60 hareket (çoğu eşleşmiş ve ERP'de fiş). Test
hesapları (sadece bu demonun yerel veritabanında, örnek veri):

| E-posta | Rol | Şifre |
|---|---|---|
| demo@kasa.test | Yönetici | Demo-Password-2026 |
| muhasebe@kasa.test | Muhasebeci | Demo-Password-2026 |
| izleyici@kasa.test | İzleyici | Demo-Password-2026 |

## Demo videosu

`.demo/kasa-demo.webm` (2 dk 35 sn, depoya girmez): kayıttan başlayarak 13 adım, her adımda Türkçe açıklama. Playwright ile
kurulu Chrome'un ayrı, temiz bir oturumunda kaydedildi.

## Genel adresten (kasa.surakaraali.dev)

Demo kendi bilgisayarında çalışır; Cloudflare Tunnel sadece web uygulamasını (3000) dışarı açar. API, test servislerinin
`/mock` kontrolleri, Swagger, RabbitMQ ve izleme paneli dışarıdan erişilemez. Bankanın onay sayfası (`/acikbank/authorize`)
ve ERP'nin fiş sayfası (`/test-erp`) web uygulamasının arkasından sunulur.

```bash
KASA_PUBLIC_URL=https://kasa.surakaraali.dev scripts/demo.sh reset
scripts/tunnel.sh          # Ctrl+C ile kapanır
```

Genel adreste yeni şirket açmak kayıt kodu istiyor: `scripts/demo.sh` ilk genel başlatmada üretip `.demo/kayit-kodu`
dosyasına yazıyor ve her başlatmada aynısını kullanıyor; seed de oradan okuyor. Kayıt canlı gösterilecekse kodu sen
yazarsın. Yine de tüneli sadece demo sırasında açık tut.

Tek seferlik kurulum (yapıldı, 2026-11-19): `brew install cloudflared`, `cloudflared tunnel login` (Cloudflare hesabında
surakaraali.dev onayı), `cloudflared tunnel create kasa`, `cloudflared tunnel route dns kasa kasa.surakaraali.dev`.
Tünelin kimlik dosyası `~/.cloudflared/` altında; depoya girmez.

## Akış

| # | Ne yapıyorum | Ne söylüyorum |
|---|---|---|
| 1 | **Kayıt:** şirket ve yönetici. Ekip → muhasebeci davet et, bağlantıyı gizli pencerede aç, katıl | "Şirket ve ilk yönetici tek işlemde oluşuyor. Davet bağlantısı tek kullanımlık; veritabanında sadece özeti var." |
| 2 | **ERP ve cariler:** firma kodu 001 → ERP'den al | "Cari kartları ERP'den alıyorum; eşleştirme bunlara yapılacak." |
| 3 | **Banka bağlantıları:** AcikBank → bankanın onay sayfası → Onayla | "Açık bankacılık: Kasa şifreyi hiç görmüyor. State ve PKCE birlikte; state ayrıca akışı başlatan tarayıcıya bağlı." |
| 4 | KlasikBank'ı kullanıcı adı/şifreyle bağla | "Bu banka eski usul SOAP; şifre Data Protection ile şifreli saklanıyor. Tutarlar Türkçe biçimde, katı kalıpla okunuyor." |
| 5 | **Panel** ve test ERP sekmesi | "Hareketlerin çoğu IBAN ya da vergi numarasıyla eşleşti ve birkaç saniyede ERP'de fiş oldu: outbox, RabbitMQ, ayrı aktarım servisi." |
| 6 | Terminalde canlı bir ödeme ekle (aşağıda), Banka bağlantıları → KlasikBank → **Şimdi çek**; panele geç | "SignalR ile ekran kendiliğinden güncellendi. Panel Redis'te önbellekli, değişince sürüm anahtarı artıyor." |
| 7 | **Eşleşmeyenler:** KUZEY LOJİSTİK satırı | "IBAN bir cariyi, vergi numarası başka bir cariyi gösteriyor; kural karar vermiyor, kişiye bırakıyor." Elle eşle → ERP sekmesinde yeni fiş |
| 8 | Muhasebeci penceresinde aynı satırı başka cariye eşlemeye çalış | "rowversion: siz bakarken değişti, 409. Kimse kimsenin kararını sessizce ezmiyor." |
| 9 | **ERP'yi kapat** (aşağıda), bir hareketi eşle → "ERP'ye gidiyor" bekliyor; ERP'yi aç → birkaç saniyede aktarılıyor | "İş kuyrukta bekledi. Aynı mesaj iki kez gelse de Idempotency-Key ile tek fiş." |
| 10 | **Redis'i kapat** (aşağıda) → paneli yenile: sistem kartında Redis "kısıtlı", panel çalışıyor; Redis'i aç | "Redis hızlandırıcı, zorunluluk değil. Kilit yokken bile çift kaydı veritabanı engelliyor; bunun testi var." |
| 11 | **İzleme paneli:** son "PUT api/transactions/.../match" izi | "Tek iz: tıklamadan ERP fişine kadar dört servis. İz outbox satırında saklanıp yayında devam ediyor." |
| 12 | **Asistan** (her ekranda sağ altta, ya da ⌘K): "Bu ay en çok kim ödedi?", "Eşleşmeyenler en çok kimden?" (anahtar varsa) | "Model SQL yazmıyor; beş okuma aracı var, şirket token'dan, IBAN maskeli." |
| 13 | Muhasebeci yerine **izleyici** davet et, onunla gir: eşleme butonu yok | "Arayüzde yok, API'de de 403; yetki sunucuda." |

## Terminal komutları

```bash
# Canlı bir tahsilat: test bankasında ABC Tekstil'den 45.000 TL
curl -s -X POST localhost:5102/mock/klasikbank/movements -H 'content-type: application/json' \
  -d '{"amount":45000,"description":"FATURA TAHSILATI","counterpartyName":"ABC TEKSTİL SANAYİ A.Ş.","counterpartyIban":"TR340004605555000011112222"}'
```

```bash
curl -s -X POST localhost:5103/mock/fault -H 'content-type: application/json' -d '{"mode":"down"}'   # ERP'yi kapat
```

```bash
curl -s -X POST localhost:5103/mock/fault -H 'content-type: application/json' -d '{"mode":"none"}'   # ERP'yi aç
```

```bash
docker compose stop redis      # Redis'i kapat
```

```bash
docker compose start redis     # Redis'i aç
```

```bash
curl -s -X POST localhost:5102/mock/klasikbank/fault -H 'content-type: application/json' -d '{"mode":"baddata"}'   # banka bozuk veri göndersin
```

## Bir şey ters giderse

- Servislerin logları: `.demo/logs/`. Her şeyi baştan başlatmak: `scripts/demo.sh reset`.
- Asistan kapalıysa (anahtar yok) arayüz bunu söylüyor; 12. adım atlanır, testleri anlatılır (sahte Claude sunucusu).
- Yedek: aynı akışın ekran kaydı (aşağıda).

## Yedek video

Hazır: `.demo/kasa-demo.webm`. Kendi sesinle anlatımlı bir kayıt istersen aynı akışı Cmd+Shift+5 ile kaydedebilirsin.
