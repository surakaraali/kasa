# Teknik sunum konuşma notu

Sunum: [sunum.surakaraali.dev/teknik](https://sunum.surakaraali.dev/teknik/), 19 slayt. Kaynağı
[teknik-sunum/index.html](teknik-sunum/index.html).

Dinleyici ekibin teknik lideri ve geliştiriciler. Her slaytta tek fikir söyle, ayrıntıya soru gelirse in. Atlanan bir
slayta soru gelirse 1. slayttaki gündemden ya da üst çubuktaki bölümlerden geç.

9-14. slaytlarda önce terimi söyle, hemen ardından altındaki benzetmeyi kur: "Buna outbox deniyor; restorandaki sipariş
fişi gibi…" Dinleyici terimi bilir, senin anladığını görmek ister.

## Süre

| Yol | Slaytlar | Süre |
|---|---|---|
| Kısa | 1, 2, 3, 4, 5, 6, 9, 12, 16, 17, 18, 19 | yaklaşık 14 dk |
| Tam | 1-19 | yaklaşık 29 dk |

Süre baştan söylenmezse sor: "Kaç dakikam var?" Yarım saatten azsa kısa yolu kullan.

## 1. Kısaca ben (45 sn)
Eğitim, stajlar, şu an .NET backend. Yapay zekâyı ve ajanları verimli kullandığını, işe tasarım ve mimariden
başladığını söyle. Gündemi okuma, tek cümle yeter: "Problemden başlayıp uygulamayı kısaca
göstereceğim, sonra mimariye, hata senaryolarına ve kararlara gideceğim."
Son cümleyle Kasa'ya geç.

## 2. Kapak (30 sn)
"Kasa, birkaç bankadaki hareketi toplayıp cariyle eşleştiren ve ERP'ye tek fiş yazan çok şirketli bir .NET uygulaması."
Ekstre kendini anlatıyor: eşleşen satır fiş numarası alıyor, iki farklı cariyi gösteren Kuzey Lojistik kişiye kalıyor.
Soldaki dört satır projenin içeriği: hangi bankalar, ERP ile ne alınıp verildiği, roller ve teknoloji. Okuma, göz
gezdirsinler. Alttaki sayılara girme, ileride geliyorlar.

## 3. Problem (1 dk)
Muhasebeci her sabah her bankadan ekstre indiriyor, açıklamaya bakıp cariyi tahmin ediyor, ERP'ye elle fiş giriyor. En
pahalı hata paranın yanlış cariye yazılması. Sağdaki "Nasıl" sütunu sunumun haritası.

## 4. Kasa'yı kullanırken (2 dk, kısa yolda 1)
Kayıtlar kendiliğinden sırayla oynar; sen üstüne konuş. Bir adıma tıklayınca o kayıt baştan oynar, "Durdur" ya da videoya
tıklamak durdurur.
1. "Bankada yeni bir tahsilat var. Şimdi çek diyorum; hareket geliyor, IBAN'dan ABC Tekstil'e eşleşiyor ve ERP fiş numarası
   alıyor. Muhasebecinin elle yaptığı üç iş, tek tıkla."
2. "Kuzey Lojistik'te IBAN bir cariyi, vergi numarası başka bir cariyi gösteriyor. Kural tahmin etmiyor, nedenini yazıp bana
   bırakıyor. Doğru cariyi seçip eşliyorum."
3. "Elle eşlediğim hareket de fiş numarasını aldı. Test ERP'sinde fiş bir kez yazılmış."
4. "Sayman'a Türkçe soruyorum. Cevabı salt okunur araçlarla buluyor; veriyi değiştiremez. Nasıl güvenli olduğunu 15.
   slaytta anlatacağım."

Kısa yolda ilk iki kaydı göster, sonra mimariye geç.

## 5. Mimari (2 dk, kısa yolda 1,5)
Modüler monolit ve tek ayrı servis. Tek geliştirici ve tek veritabanı için her modülü ayırmanın ağ ve işletme bedeli
faydasından büyük. ERP'ye yazan kısım ayrı, çünkü ERP yavaşlayabilir ve kapanabilir; kullanıcı istekleri onu beklememeli.
Servisler arasındaki tek sözleşme RabbitMQ mesajı. SQL Server asıl veri, Redis hızlandırıcı, MongoDB ham banka cevapları.
Karar 0001.

## 6. Bir hareketin yolu (2 dk, kısa yolda 1,5)
Quartz senkronu başlatır, Kasa.Api bankadan hareketleri çeker. Hareket, eşleşme ve outbox satırı tek işlemde SQL Server'a
yazılır. Outbox yayınlanır, ErpExporter fişi `Idempotency-Key` ile ERP'ye yazar, sonuç geri gelir ve hareket "aktarıldı"
olur. Tıklamadan ERP fişine kadar her şey tek OpenTelemetry izinde.

## 7. Bozuk veri, çift kayıt (2 dk, kısa yolda yok)
Her banka kendi katı kalıbıyla okunuyor, tutar `InvariantCulture` ile. Bir satır bozuksa hesabın bütün cevabı reddediliyor,
ham cevap MongoDB'de 90 gün duruyor. Çift kaydı `(BankAccountId, BankReference)` benzersiz indeksi engelliyor. Redis kilidi
yalnızca boşa iş yapılmasını önlüyor; kilidin süresi dolabilir ya da Redis düşebilir, doğruluk veritabanında. Karar 0007.

## 8. Eşleştirme (1,5 dk, kısa yolda yok)
Önce IBAN, sonra vergi no. Çelişki ya da belirsizlikte kural karar vermiyor, kişiye bırakıyor: yanlış cariye yazılan para,
henüz yazılmamış paradan kötü. Kurallar saf fonksiyon, veritabanısız birim testli. İki kişi aynı satırı eşlerse rowversion
ile ikincisi 409 alıyor. Karar 0010.

## 9. Outbox (2 dk, kısa yolda 1,5)
Önce sol tarafı oynat: COMMIT'ten sonra süreç düşerse mesaj hiç gitmez, kimse fark etmez. Outbox mesajı eşleşmeyle aynı
işleme koyuyor. Sağ tarafta iki API örneği aynı satırı almasın diye `UPDLOCK, READPAST, ROWLOCK`. 2PC yok, çünkü RabbitMQ
dağıtık işlemi desteklemiyor. "Mesaj iki kez giderse?" sorusu 11. slaytta. Karar 0009.

**Benzetme, restoran:** Kasiyer ödemeyi alıyor, siparişi mutfağa iletmeden ayrılıyor; para alınmış, yemek hiç gelmiyor.
Outbox'ta sipariş fişi ödemeyle aynı anda yazılıp tezgâha bırakılıyor. Bir garson birkaç saniyede bir tezgâha bakıp
fişleri mutfağa taşıyor; garson düşse bile fişler tezgâhta bekliyor. `UPDLOCK, READPAST`: iki garson aynı fişi almasın
diye, biri fişe elini koyunca öbürü beklemeden sıradakine geçiyor.
Cümle: "Mesajı eşleşmeyle aynı işlemde yazıyorum. Ya ikisi birden olur ya hiçbiri."

## 10. Kuyruklar (1,5 dk, kısa yolda yok)
Üç düğmeye sırayla bas. ERP kapalıysa mesaj bekleme kuyruğuna gider, 10 saniyede bir, en çok 5 kez denenir. ERP
reddediyorsa dead-letter kuyruğuna gider ve kişi görür. Geçici hata bekler, kalıcı hata kişiye gider. Exporter elle ack
veriyor ve sonucu ack'ten önce yayınlıyor.

**Benzetme, kargo:** Kapı kapalıysa (ERP kapalı) kurye not bırakıp tekrar geliyor: 10 saniye arayla en çok 5 kez. Yine
kimse yoksa paket şubeye dönüyor, alıcı gidip alıyor; Kasa'da bu, kişinin "yeniden gönder" demesi. Adres yanlışsa (ERP
reddediyor) tekrar gitmenin anlamı yok; paket hemen şubeye dönüyor, gönderici adresi düzeltiyor.
Cümle: "Geçici hata bekler, kalıcı hata kişiye gider. Hiçbir mesaj sessizce kaybolmaz."

## 11. Idempotency (1,5 dk, kısa yolda yok)
Önce "Anahtarsız": ERP'de 2 fiş. Sonra "Anahtarla": 1 fiş. Her teslimde aynı `ExportId` başlıkta gidiyor, ERP kaç kez
sorulursa sorulsun tek fiş yazıyor. "En az bir kez" teslimin bedeli bu tek satır.

**Benzetme, online ödeme:** "Ödeme yap"a iki kez basınca kartından iki kez çekilmemesi; mağaza sipariş numarasına bakıp
"bunu zaten aldım" diyor. Mesaj neden iki kez gelir: ERP fişi yazdıktan hemen sonra servis çökerse kuyruk aynı mesajı
tekrar verir. ERP aktarım numarasını hatırlıyor, ikincisinde yeni fiş yazmak yerine eskisini döndürüyor.
Cümle: "Anahtarsız iki fiş, anahtarla tek fiş."

## 12. Bir parçayı kapatalım (2 dk, kısa yolda 1,5)
ERP, RabbitMQ, Redis, MongoDB ve Banka düğmeleri. Her birinde neyin çalışıp neyin durduğu ve altında testin adı var. 18
senaryonun hepsi gerçek altyapıya karşı testli. `/health`: Redis, MongoDB ya da RabbitMQ yoksa degraded, SQL Server yoksa
unhealthy. Canlı demo için en iyi an bu slayt (aşağıda).

**Benzetme, restoran mutfağı:** Bulaşık makinesi bozulunca restoran kapanmaz; bulaşık elde yıkanır, işler yavaşlar. Mutfak
yoksa restoran kapanır. Kasa'nın mutfağı SQL Server: o yoksa unhealthy, diğerlerinden biri yoksa degraded, yani çalışıyor
ama bir şeyi eksik. Arabada motor arızası ile yanan uyarı lambası arasındaki fark gibi. Düğmeler: ERP kapalıysa fişler
bekler, RabbitMQ kapalıysa mesajlar veritabanında bekler, Redis kapalıysa yavaşlar ama yanlış yapmaz, MongoDB kapalıysa
yalnız ham arşiv atlanır, banka kapalıysa hata kaydedilir ve sonraki çekişte eksikler alınır.
Cümle: "18 senaryonun her biri, o parçayı gerçekten kapatan bir otomatik testle kanıtlı."

## 13. Ölçümler (2 dk, kısa yolda yok)
500 bin hareket, 7 çalıştırmanın ortancası. 250.000. satırdan sonraki sayfa yalnız SQL'de OFFSET ile 46,9 ms, keyset ile
1,3 ms. Panel önbelleksiz 324,6 ms, Redis'ten 2,9 ms; önbellek SQL'deki sürümle geçersiz kılınıyor. "Gerçek ortamda da
böyle mi?" sorusunun cevabı: hayır, yerel makine ve ağ yok, amaç iki yöntemi aynı koşulda karşılaştırmak.

**Benzetme, kitap ve rapor:** 250.000. satıra gitmek, kalın bir kitapta ileri bir sayfaya gitmek gibi. OFFSET baştan satır
sayarak ilerliyor (46,9 ms), keyset ayracı koyduğun yerden devam ediyor (1,3 ms); veri büyüdükçe fark büyüyor. Panel bir
özet rapor: her açılışta baştan hesaplamak 324,6 ms, hesaplanmış hâlini Redis'ten vermek 2,9 ms. Yeni hareket gelince
raporun sürüm numarası değişiyor, eski rapor bir daha kullanılmıyor; panel hızlı ama eski veri göstermiyor.
Cümle: "Hızlı dediğimi ölçtüm; kendi bilgisayarımda, iki yöntemi aynı koşulda karşılaştırarak."

## 14. Güvenlik (2 dk, kısa yolda yok)
Şirket izolasyonu iki katmanlı: her `ITenantEntity` için sorgu filtresi ve `SaveChanges` koruması. Şirket yalnızca
token'dan gelir, başka şirketin id'si 404. Yenileme anahtarı rotasyonu: kullanılmış anahtar tekrar gelirse bütün oturum
ailesi iptal. Karar 0003 ve 0004.

**Benzetme, otel:** Her şirket bir oda, giriş kartı (token) yalnız kendi odasını açıyor. Kontrol iki yerde: kapıdaki kart
okuyucu (her sorgu yalnız o şirketin verisini görür) ve çıkıştaki görevli (başka şirkete kayıt yazılmaya çalışılırsa
engeller). Başka şirketin kaydını isteyen "erişim yok" değil "böyle bir şey yok" (404) cevabı alıyor; resepsiyon odanın
varlığını bile söylemiyor. Yenileme anahtarı: otel kartı her kullanımda yenisiyle değiştiriliyor, eskisi iptal. Eski kart
tekrar okutulursa kart kopyalanmış demektir; o odanın bütün kartları iptal edilir, herkes yeniden kimlik gösterir.
Cümle: "Şirket bilgisi yalnızca token'dan gelir, istekten asla."

## 15. Sayman (1,5 dk, kısa yolda yok)
Model SQL yazmıyor, ne istediğini söylüyor; sorguyu Kasa yazıp çalıştırıyor. 5 salt okunur araç, şirket JWT'den, IBAN
maskeli. Ekrandaki JSON'da banka açıklamasına gizlenmiş bir talimat var: model uysa bile yalnızca kendi şirketinin
verisini okuyabilir. Karar 0013.

## 16. Kararlar (1 dk)
Tablodan tek satır anlat. Örnek: "Redis zorunlu değil, hızlandırıcı. Bedeli: Redis kapalıyken deneme sınırları sunucu
başına sayılıyor." 14 karar notu var, bilerek ertelenenler kodda `shortcut:` notuyla işaretli.

## 17. Çalışma düzeni (1 dk)
Animasyon kendiliğinden Sprint 3'ü oynatır: kartlar Backlog'dan Ready'ye, oradan tek tek In Progress, In Review ve Done'a
geçer. Kartlar ve sprintler panodaki gerçek işler. Sprint 7'ye bas: "Teknik sunum" ve "Medium yazıları" hâlâ In Progress,
yani şu an bu sunumdayız.
Cümle: "Kasa'yı tek başıma geliştirdim ama işi takımdaki gibi planladım: her sprint bir kilometre taşı, her iş bir kart.
Stajımda mobil ekipte de Jira'da benzer bir panoyla çalıştım."

Dürüst ol, sorulursa:
- **"In Review'da kim inceledi?"** "Tek başımaydım; incelemeyi CI'daki testler ve ben yaptım. Takımda bu adım kod incelemesi
  olur, stajda öyle çalıştık."
- **"Sprintler ne kadar sürdü?"** Kartların hepsi aynı gün açılıp kapandı, panoda bu görünüyor. "Yapay zekâyla hızlı
  ilerlediğim için sprintler kısa sürdü. Amaç takvim değil, işi küçük ve test edilebilir parçalara bölmekti."
- Pano gizli; isterlerse ekranından açıp gösterebilirsin.

## 18. Kasa dışında yaptıklarım (45 sn)
İki cümle yeter, ayrıntıya soru gelirse in.
- **özgeç:** "İlanı ve CV'yi tarayıcıda karşılaştırıyor, eksik becerileri ve ATS'nin yanlış okuyacağı yerleri gösteriyor.
  Sunucu yok, CV hiçbir yere yüklenmiyor; gizliliği tasarımın başına koydum."
- **Dilek Galaksisi:** "Kayıt olmadan dilek yazılan küçük bir sosyal site. Herkese açık olduğu için küfür ve spam filtresi,
  günlük sınır var; IP adresini saklamıyorum. Cloudflare Workers ve D1 üzerinde."
- **Gedikli Hukuk ve FLADVart:** "İkisi de Next.js ile yaptığım, yayında olan siteler. İletişim formlarından gelen e-postalar
  Resend ile gidiyor. FLADVart'ta içerik, NextAuth ile korunan bir yönetim panelinden PostgreSQL'e yazılıyor."
  Bu iki satır ilandaki web ve entegrasyon tarafına bağlanıyor; soru gelirse formdan gelen isteğin sunucuda doğrulanıp
  Resend API'sine nasıl gittiğini anlat.

## 19. Teşekkürler (30 sn)
"İsterseniz ERP'yi birlikte kapatalım." Demoya ya da sorulara geç.

## Demo

Akış ve komutlar [demo.md](demo.md) içinde; sunumda 9. adımı (ERP'yi kapat) ve gerekirse 10. adımı (Redis'i kapat) göster.

- Ekran paylaşımında demoyu `http://localhost:3000` adresinden göster. Canlı adres Cloudflare'den geçiyor ve 10 saniyede
  yaklaşık 20 API isteğini aşan adresi kısa süre bekletiyor; normal hızda gezinmede bu sınıra gelinmiyor.
- Görüşmeden önce Docker ve demo ayakta olsun, veri temiz olsun (`scripts/demo.sh reset`, ardından `scripts/demo-seed.sh`).
  Canlı adresten göstereceksen iki komutun başına da `KASA_PUBLIC_URL=https://kasa.surakaraali.dev` ekle.
- ERP kapalıyken 5 deneme 10 saniye arayla yapılıyor. ERP'yi yaklaşık 40 saniye içinde aç, yoksa hareket "aktarılamadı"
  olur ve yeniden göndermen gerekir.
- Demo açılmazsa 4. slayttaki kayıtlar uygulamanın kendisini, 10. ve 12. slayttaki sahneler ERP kapalıyken olanı gösterir.

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
