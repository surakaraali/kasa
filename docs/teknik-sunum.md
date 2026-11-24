# Teknik sunum konuşma notu

Sunum: [sunum.surakaraali.dev/teknik](https://sunum.surakaraali.dev/teknik/), 16 slayt. Kaynağı
[teknik-sunum/index.html](teknik-sunum/index.html).

Dinleyici ekibin teknik lideri ve geliştiriciler. Her slaytta tek fikir söyle, ayrıntıya soru gelirse in. Atlanan bir
slayta soru gelirse 1. slayttaki tablodan ya da üst çubuktaki bölümlerden geç.

## Süre

| Yol | Slaytlar | Süre |
|---|---|---|
| Kısa | 1, 2, 3, 4, 5, 8, 11, 15, 16 | yaklaşık 10 dk |
| Tam | 1-16 | yaklaşık 24 dk |

Süre baştan söylenmezse sor: "Kaç dakikam var?" Yarım saatten azsa kısa yolu kullan.

## 1. Kısaca ben (45 sn)
Eğitim, stajlar, şu an .NET backend. Tabloyu okuma: "İlanın istediklerini Kasa'da nerede gösterdiğimi burada topladım."
Son cümleyle Kasa'ya geç.

## 2. Kapak (30 sn)
"Kasa, birkaç bankadaki hareketi toplayıp cariyle eşleştiren ve ERP'ye tek fiş yazan çok şirketli bir .NET uygulaması."
Ekstre kendini anlatıyor: eşleşen satır fiş numarası alıyor, iki farklı cariyi gösteren Kuzey Lojistik kişiye kalıyor.
Alttaki sayılara girme, ileride geliyorlar.

## 3. Problem (1 dk)
Muhasebeci her sabah her bankadan ekstre indiriyor, açıklamaya bakıp cariyi tahmin ediyor, ERP'ye elle fiş giriyor. En
pahalı hata paranın yanlış cariye yazılması. Sağdaki "Nasıl" sütunu sunumun haritası.

## 4. Mimari (2 dk, kısa yolda 1,5)
Modüler monolit ve tek ayrı servis. Tek geliştirici ve tek veritabanı için her modülü ayırmanın ağ ve işletme bedeli
faydasından büyük. ERP'ye yazan kısım ayrı, çünkü ERP yavaşlayabilir ve kapanabilir; kullanıcı istekleri onu beklememeli.
Servisler arasındaki tek sözleşme RabbitMQ mesajı. SQL Server asıl veri, Redis hızlandırıcı, MongoDB ham banka cevapları.
Karar 0001.

## 5. Bir hareketin yolu (2 dk, kısa yolda 1,5)
Quartz senkronu başlatır, Kasa.Api bankadan hareketleri çeker. Hareket, eşleşme ve outbox satırı tek işlemde SQL Server'a
yazılır. Outbox yayınlanır, ErpExporter fişi `Idempotency-Key` ile ERP'ye yazar, sonuç geri gelir ve hareket "aktarıldı"
olur. Tıklamadan ERP fişine kadar her şey tek OpenTelemetry izinde.

## 6. Bozuk veri, çift kayıt (2 dk, kısa yolda yok)
Her banka kendi katı kalıbıyla okunuyor, tutar `InvariantCulture` ile. Bir satır bozuksa hesabın bütün cevabı reddediliyor,
ham cevap MongoDB'de 90 gün duruyor. Çift kaydı `(BankAccountId, BankReference)` benzersiz indeksi engelliyor. Redis kilidi
yalnızca boşa iş yapılmasını önlüyor; kilidin süresi dolabilir ya da Redis düşebilir, doğruluk veritabanında. Karar 0007.

## 7. Eşleştirme (1,5 dk, kısa yolda yok)
Önce IBAN, sonra vergi no. Çelişki ya da belirsizlikte kural karar vermiyor, kişiye bırakıyor: yanlış cariye yazılan para,
henüz yazılmamış paradan kötü. Kurallar saf fonksiyon, veritabanısız birim testli. İki kişi aynı satırı eşlerse rowversion
ile ikincisi 409 alıyor. Karar 0010.

## 8. Outbox (2 dk, kısa yolda 1,5)
Önce sol tarafı oynat: COMMIT'ten sonra süreç düşerse mesaj hiç gitmez, kimse fark etmez. Outbox mesajı eşleşmeyle aynı
işleme koyuyor. Sağ tarafta iki API örneği aynı satırı almasın diye `UPDLOCK, READPAST, ROWLOCK`. 2PC yok, çünkü RabbitMQ
dağıtık işlemi desteklemiyor. "Mesaj iki kez giderse?" sorusu 10. slaytta. Karar 0009.

## 9. Kuyruklar (1,5 dk, kısa yolda yok)
Üç düğmeye sırayla bas. ERP kapalıysa mesaj bekleme kuyruğuna gider, 10 saniyede bir, en çok 5 kez denenir. ERP
reddediyorsa dead-letter kuyruğuna gider ve kişi görür. Geçici hata bekler, kalıcı hata kişiye gider. Exporter elle ack
veriyor ve sonucu ack'ten önce yayınlıyor.

## 10. Idempotency (1,5 dk, kısa yolda yok)
Önce "Anahtarsız": ERP'de 2 fiş. Sonra "Anahtarla": 1 fiş. Her teslimde aynı `ExportId` başlıkta gidiyor, ERP kaç kez
sorulursa sorulsun tek fiş yazıyor. "En az bir kez" teslimin bedeli bu tek satır.

## 11. Bir parçayı kapatalım (2 dk, kısa yolda 1,5)
ERP, RabbitMQ, Redis, MongoDB ve Banka düğmeleri. Her birinde neyin çalışıp neyin durduğu ve altında testin adı var. 18
senaryonun hepsi gerçek altyapıya karşı testli. `/health`: Redis, MongoDB ya da RabbitMQ yoksa degraded, SQL Server yoksa
unhealthy. Canlı demo için en iyi an bu slayt (aşağıda).

## 12. Ölçümler (2 dk, kısa yolda yok)
500 bin hareket, 7 çalıştırmanın ortancası. 250.000. satırdan sonraki sayfa yalnız SQL'de OFFSET ile 46,9 ms, keyset ile
1,3 ms. Panel önbelleksiz 324,6 ms, Redis'ten 2,9 ms; önbellek SQL'deki sürümle geçersiz kılınıyor. "Gerçek ortamda da
böyle mi?" sorusunun cevabı: hayır, yerel makine ve ağ yok, amaç iki yöntemi aynı koşulda karşılaştırmak.

## 13. Güvenlik (2 dk, kısa yolda yok)
Şirket izolasyonu iki katmanlı: her `ITenantEntity` için sorgu filtresi ve `SaveChanges` koruması. Şirket yalnızca
token'dan gelir, başka şirketin id'si 404. Yenileme anahtarı rotasyonu: kullanılmış anahtar tekrar gelirse bütün oturum
ailesi iptal. Karar 0003 ve 0004.

## 14. Sayman (1,5 dk, kısa yolda yok)
Model SQL yazmıyor, ne istediğini söylüyor; sorguyu Kasa yazıp çalıştırıyor. 5 salt okunur araç, şirket JWT'den, IBAN
maskeli. Ekrandaki JSON'da banka açıklamasına gizlenmiş bir talimat var: model uysa bile yalnızca kendi şirketinin
verisini okuyabilir. Karar 0013.

## 15. Kararlar (1 dk)
Tablodan tek satır anlat. Örnek: "Redis zorunlu değil, hızlandırıcı. Bedeli: Redis kapalıyken deneme sınırları sunucu
başına sayılıyor." 14 karar notu var, bilerek ertelenenler kodda `shortcut:` notuyla işaretli.

## 16. Teşekkürler (30 sn)
"İsterseniz ERP'yi birlikte kapatalım." Demoya ya da sorulara geç.

## Demo

Akış ve komutlar [demo.md](demo.md) içinde; sunumda 9. adımı (ERP'yi kapat) ve gerekirse 10. adımı (Redis'i kapat) göster.

- Görüşmeden önce Docker ve demo ayakta olsun, veri temiz olsun (`scripts/demo.sh reset`, ardından `scripts/demo-seed.sh`).
  Canlı adresten göstereceksen iki komutun başına da `KASA_PUBLIC_URL=https://kasa.surakaraali.dev` ekle.
- ERP kapalıyken 5 deneme 10 saniye arayla yapılıyor. ERP'yi yaklaşık 40 saniye içinde aç, yoksa hareket "aktarılamadı"
  olur ve yeniden göndermen gerekir.
- Demo açılmazsa 9. ve 11. slayttaki sahneler aynı senaryoyu internetsiz gösterir.

## "Canlıya çıksa ne eklerdin?"

Hepsi kodda `shortcut:` notu ya da [guvenlik.md](guvenlik.md) içinde "Eksik" olarak yazılı.

- **Birden çok API örneği:** migration'ları açılıştan dağıtım adımına almak, SignalR için Redis backplane, Redis kapalıyken
  de paylaşılan deneme sınırı.
- **Sırlar:** Data Protection anahtar halkasını sertifika ya da anahtar kasasıyla korumak, JWT anahtarını gizli ayar
  deposundan almak.
- **Web:** nonce'lu CSP ile betiklerde `'unsafe-inline'` kaldırmak.
- **Tedarik zinciri:** CI'da Dependabot ya da `npm audit` ve `dotnet list package --vulnerable`.
- **Büyüyen veri:** gönderilmiş outbox satırlarını temizleyen iş, `LIKE` araması yerine full-text search.
- **Kapsam:** döviz hesapları; şu an yalnız TL hesaplar destekleniyor.

## "Yapay zekâyla mı yaptın?"

Evet, Claude Code ile; işin nasıl bölündüğü [ai-assisted-development.md](ai-assisted-development.md) içinde. Kısaca: problemi,
kapsamı ve sınırları ben seçtim; planı, veri modelini ve kodu Claude Code önerdi ve yazdı. Planı onayladım, sonuçları
gözden geçirdim, yön değiştirmek istediğimde durdurup yeniden yönlendirdim. Her iddia bir test ya da ölçümle kanıtlı.

Bu soruya kendi yaşadığın iki somut örnekle hazırlan: yönlendirdiğin ya da düzelttirdiğin bir an ve en iyi anladığın bir
karar.

## Olası sorular
[sorular.md](sorular.md) içinde sprint sprint sorular ve ipuçları.
