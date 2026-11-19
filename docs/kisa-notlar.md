# Kasa: bilmen gereken küçük şeyler

Sunumdan önce bir kez oku. Her madde "ne oluyor" ve kısaca "neden".

## Giriş ve oturum

- Giriş anahtarı 15 dakika geçerli ve sadece tarayıcının belleğinde duruyor. Sayfa yenilenince arka plandaki çerezle sessizce yenisi alınıyor.
- O çerez JavaScript'le okunamıyor (httpOnly). Bir saldırgan sayfaya kod soksa bile çalamaz.
- Yenileme anahtarı tek kullanımlık. Çalınıp ikinci kez kullanılırsa sistem bunu fark ediyor ve o oturumu tamamen kapatıyor.
- 5 yanlış şifrede hesap 15 dakika kilitleniyor. Aynı internet adresinden dakikada en fazla 10 deneme yapılabiliyor.
- Olmayan bir e-postayla giriş denenirse de "e-posta ya da şifre hatalı" deniyor, aynı sürede. Böylece hangi e-postanın kayıtlı olduğu anlaşılamıyor.
- Şifre en az 10 karakter, büyük harf, küçük harf ve rakam içermeli.
- Kayıtta şirket ve ilk yönetici tek seferde oluşuyor. Şifre kurala uymazsa şirket de oluşmuyor; yarım kayıt kalmıyor.

## Profil

- Ad soyad değişiyor. E-posta ve rol değişmiyor: e-posta için doğrulama gerekir, rolü yönetici belirler.
- Şifre değiştirmek için mevcut şifre şart. Açık unutulmuş bir oturumla başkası şifreyi değiştiremesin diye.
- Yeni şifre iki kez soruluyor (yazım hatasına karşı).
- Şifre değişince diğer bütün cihazlardaki oturumlar kapanıyor, kendi oturumun açık kalıyor.
- Bilinçli bir sınır: diğer cihazlarda zaten açılmış sayfaların giriş anahtarı en fazla 15 dakika daha çalışabiliyor.

## Ekip ve roller

- **Yönetici:** her şey.
- **Muhasebeci:** banka bağlar, eşleme yapar, "Şimdi çek" der, cari kartları alır.
- **İzleyici:** sadece görür.
- Banka bağlantısını kaldırmak, ERP firma kodunu değiştirmek ve ekip yönetimi sadece yöneticide.
- Yetki sunucuda kontrol ediliyor. İzleyici düğmeyi görmese de API'yi doğrudan denerse yine "yetkiniz yok" (403) alıyor.
- Davet bağlantısı 7 gün geçerli ve tek kullanımlık. Bir kez gösteriliyor; veritabanında bağlantının kendisi değil, sadece geri çevrilemeyen özeti (hash) var.

## Şirketlerin birbirinden ayrılması

- Bir şirket başka bir şirketin hiçbir verisini göremiyor.
- Başka şirketin bir kaydı adresle denenirse "bulunamadı" (404) dönüyor, "yetkiniz yok" değil. Kaydın var olduğu bile belli olmuyor.
- Hangi şirket olduğu her zaman giriş bilgisinden okunuyor, kullanıcının gönderdiği veriden değil.
- Geliştirici sorguda şirket koşulunu unutsa bile sistem kendisi ekliyor.

## Bankalar

- **AcikBank (açık bankacılık):** onay bankanın kendi sayfasında veriliyor, Kasa şifreyi hiç görmüyor. Rıza 90 gün geçerli.
- Bankadan dönüş, işlemi başlatan tarayıcıdan gelmezse reddediliyor. Başkasına onaylatılan bir bağlantıyla veri çalınamasın diye.
- **KlasikBank (eski usul):** kullanıcı adı ve şifreyle bağlanıyor. Şifre önce bankada deneniyor; yanlışsa hiçbir şey kaydedilmiyor. Doğruysa şifreli saklanıyor.
- Aynı bankaya ikinci bir bağlantı açılamıyor.
- Hareketler 15 dakikada bir kendiliğinden geliyor. "Şimdi çek" ile hemen de alınabiliyor.
- Aynı anda iki çekim başlarsa ikincisi "bu bağlantı şu anda zaten çekiliyor" diyor.
- İlk bağlantıda son 30 günün hareketleri geliyor. Sonraki her çekimde son 2 gün yeniden okunuyor, çünkü bankalar bazen bir hareketi geç işliyor.
- Aynı hareket asla iki kez kaydedilmiyor. Bankanın referans numarası tek kabul ediliyor; büyük/küçük harf fark ediyor.
- Banka bozuk bir tutar gönderirse ("1250.50" ya da "12,5O") o cevaptaki hiçbir hareket kaydedilmiyor. Yarım ekstre, yanlış ekstredir.
- Banka 10 saniyede cevap vermezse "kapalı" sayılıyor. Hata bağlantı kartında görünüyor, sonraki çekim telafi ediyor.
- Bankada rıza iptal edilirse bağlantı "Yeniden bağlanmalı" oluyor. Yeniden bağlanınca eski hesaplar ve geçmiş korunuyor.
- Bağlantı kaldırılınca anahtarlar ve şifre siliniyor, bankadaki rıza iptal ediliyor. Gelmiş hareketler kalıyor; onlar şirketin kaydı.
- Bankaların gönderdiği ham cevaplar ayrıca saklanıyor ("banka o gün tam olarak ne gönderdi?" sorusu için). 90 gün sonra kendiliğinden siliniyor.

## Eşleştirme

- Önce IBAN'a, sonra vergi numarasına bakılıyor. Ancak tek bir cari uyuyorsa eşleniyor.
- IBAN bir cariyi, vergi numarası başka bir cariyi gösteriyorsa eşlenmiyor, kişiye bırakılıyor. Yanlış cariye yazılan para, hiç yazılmamış paradan kötü.
- Cari kartlar ERP'den sonradan alınırsa, bekleyen bütün hareketler yeniden deneniyor.
- Elle eşlemede "ERP'ye aktarma" seçeneği var; banka masrafları gibi bir cariye ait olmayanlar için.
- İki kişi aynı hareketi aynı anda eşlerse ilki kazanıyor, ikincisi "siz bakarken başka biri değiştirdi" uyarısını alıyor.
- ERP'ye aktarılmış bir hareketin eşleşmesi değiştirilemiyor. Düzeltme ERP'de ters fişle yapılır.
- Her eşleme kararı kayıt altında: kim, ne zaman, hangi cariye, hangi kuralla.

## ERP'ye aktarım

- Giren para tahsilat fişi, çıkan para tediye fişi oluyor.
- Aktarım ayrı bir serviste ve kuyruk üzerinden yapılıyor. ERP yavaşlasa da ana uygulama beklemiyor.
- ERP kapalıysa fiş kaybolmuyor: 10 saniye arayla 5 kez deneniyor. Hâlâ olmazsa hareket "Aktarılamadı" oluyor.
- ERP fişi reddederse (örneğin cari ERP'de silinmişse) boşuna tekrar denenmiyor, hemen "Aktarılamadı" oluyor.
- "Aktarılamadı" olan hareket Eşleşmeyenler ekranında görünüyor. Kişi düzeltip yeniden gönderebiliyor.
- Aynı fiş isteği iki kez giderse ERP ikinci fişi yazmıyor, ilkini döndürüyor. ERP fişi yazıp cevap veremeden bağlantı kopsa bile tek fiş oluşuyor.
- Eşleşme kaydı ile "ERP'ye gönder" isteği aynı anda kaydediliyor. Biri olup diğeri olmayamaz.

## Ekranlar

- Ekranlar yenilemeden kendiliğinden güncelleniyor (yeni hareket, eşleşme, ERP sonucu).
- Panel hızlı olsun diye hafızada tutuluyor; bir şey değişince hemen yeniden hesaplanıyor. 500 bin harekette önbelleksiz 253 ms, önbellekten 1 ms.
- Hareketler listesi 500 bin harekette de ilk sayfadaki kadar hızlı (yaklaşık 5 ms). "Daha eski hareketler" düğmesi kaldığı yerden devam ediyor.
- "Eşleşmeyenler" yanındaki sayı: karar bekleyenler artı ERP'ye aktarılamayanlar.
- Paneldeki sistem durumu noktaları: yeşil çalışıyor, sarı kısıtlı (sistem yine çalışıyor), kırmızı kapalı.
- Tutarlar renkli değil, yönünü + ve − işareti söylüyor. Renk sadece karar gereken yerlerde.
- Web güncellendiğinde açık sekme hata vermiyor, gerekirse kendini bir kez yeniliyor.

## Sayman (asistan)

- Sadece okuyor, hiçbir şeyi değiştiremiyor.
- Sadece giriş yapan kişinin şirketini görebiliyor. Kim ne yazarsa yazsın başka şirkete ulaşamıyor.
- IBAN ve vergi numaralarını maskeli görüyor (TR09 **** **** 0001).
- Banka açıklamasına "önceki talimatları unut" gibi bir şey yazılmışsa onu veri sayıyor, talimat olarak almıyor.
- Rakamları kendisi hesaplamıyor; veritabanı hesaplıyor, Sayman aktarıyor.
- Kişi başına saatte 30 soru, soru başına en fazla 6 adım, en fazla 90 saniye. Maliyet kontrolü için.
- Girişteki karşılama mesajı yapay zekâdan değil panelin verisinden geliyor. Ücretsiz ve her zaman doğru. Oturum başına bir kez çıkıyor, 14 saniye sonra kendiliğinden kapanıyor.
- ⌘K (Windows'ta Ctrl+K) ile her ekrandan açılıyor. Sayfa değişse de konuşma kalıyor.
- API anahtarı tanımlı değilse "kapalı" diyor; uygulamanın geri kalanı etkilenmiyor.

## Bir şey çökerse

- **Redis kapalı:** giriş, senkron ve panel çalışıyor, sadece biraz yavaşlıyor. Çift kaydı yine veritabanı engelliyor.
- **MongoDB kapalı:** ham banka cevapları arşivlenmiyor, gerisi çalışıyor.
- **RabbitMQ kapalı:** ERP'ye gidecek işler veritabanında bekliyor, açılınca gidiyor.
- **Aktarım servisi kapalı:** işler kuyrukta bekliyor.
- **SQL Server kapalı:** sistem çalışmıyor. Asıl veri orada; bu bilinçli.
- 18 hata senaryosunun her birinin otomatik testi var. Toplam 79 test, gerçek veritabanlarına karşı çalışıyor.
- İzleme panelinde tek bir tıklamanın API, kuyruk, aktarım servisi ve ERP'deki yolu tek bir iz olarak görünüyor.

## Demo için

- Sıfırdan başlatmak: `scripts/demo.sh reset`, sonra `scripts/demo-seed.sh` (hazır şirket: Anadolu Tekstil A.Ş.).
- Hazır şirketin girişleri: demo@kasa.test, muhasebe@kasa.test, izleyici@kasa.test; şifre docs/demo.md içinde.
- Test bankası girişi: kullanıcı `kasa`, şifre `klasik-dev-pass`. Bunlar test değerleri.
- kasa.surakaraali.dev senin bilgisayarında çalışıyor; tünel açıkken adresi bilen herkes kayıt açabilir. Demodan sonra kapat.
- Bankalar ve ERP test için kurgulandı; bütün veriler örnek.

## Bilerek bırakılan eksikler (sorulursa dürüstçe)

- İki adımlı doğrulama (SMS/uygulama) ve "şifremi unuttum" yok. E-posta gönderimi yok.
- Banka şifrelerini ve anahtarlarını şifreleyen anahtar halkası şu an aynı veritabanında; gerçek kullanımda ayrı bir anahtar kasasına alınmalı. (Kullanıcı şifreleri zaten geri çevrilemeyen özet olarak saklanıyor.)
- Gönderilmiş ERP mesajlarının kayıtları silinmiyor; zamanla temizlik işi gerekir.
- Asistanın konuşmaları sunucuda saklanmıyor; tarayıcı kapanınca gidiyor.
