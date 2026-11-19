# 0006: Banka bağlantısı: açık bankacılık akışı ve sırların saklanması

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
İki çeşit banka var. AcikBank açık bankacılık konuşuyor (REST): müşteri bankanın kendi sayfasında rıza veriyor, Kasa
karşılığında kısa ömürlü bir erişim anahtarı ve tek kullanımlık bir yenileme anahtarı alıyor. KlasikBank eski usul (SOAP):
rıza akışı yok, şirketin internet bankacılığı kullanıcı adı ve şifresiyle soruluyor. İki durumda da Kasa'nın elinde bankaya
giden bir sır kalıyor. Soru şu: bu sırlar nasıl alınacak ve nerede, nasıl saklanacak?

## Seçenekler
1. **Sırları veritabanında düz metin tutmak.** En kolayı. Veritabanı yedeğini gören herkes bütün şirketlerin bankasına girer.
2. **Sütun şifreleme (SQL Server Always Encrypted).** Güçlü, ama sürücü ayarı, sertifika yönetimi ve sorgu kısıtları getiriyor.
3. **Uygulamada şifreleme (ASP.NET Core Data Protection).** Anahtar halkası bütün API örneklerinde ortak, anahtarlar 90 günde
   bir kendiliğinden yenileniyor, eski anahtarla şifrelenen veri okunmaya devam ediyor.

## Karar
**3. seçenek.** Erişim anahtarı, yenileme anahtarı, PKCE doğrulayıcısı ve SOAP bankanın kullanıcı adı/şifresi veritabanına
`IDataProtector.Protect` ile şifrelenmiş olarak yazılıyor. Anahtar halkası da SQL Server'da (`DataProtectionKeys`), böylece
birden fazla API örneği aynı veriyi çözebiliyor.

Açık bankacılık akışı:
1. Kasa bankada rıza oluşturur (90 gün; bankanın sınırı 180 gün).
2. Rastgele bir **state** ve **PKCE doğrulayıcısı** üretilir. State'in sadece SHA-256 özeti saklanır.
3. Tarayıcı bankanın onay sayfasına gider. Bankanın yönlendirme adresi bankada önceden kayıtlı; başka adrese kod gönderilmez.
4. Banka tarayıcıyı Kasa'nın geri dönüş adresine kodla yollar. Kasa kodu doğrulayıcıyla birlikte anahtarlara çevirir.
   Kod çalınsa bile doğrulayıcı olmadan işe yaramaz.
5. Geri dönüş isteğinde oturum yok (bankadan gelen bir yönlendirme). Şirket, state'in işaret ettiği bağlantıdan bulunur.
   Akışı başlatan tarayıcıya httpOnly bir çerez (`kasa_bank_state`) bırakılır ve geri dönüşte state ile karşılaştırılır.
   Bu olmasa saldırgan kendi şirketi için akışı başlatıp kurbana onaylatabilir, kurbanın banka verisi saldırganın şirketine
   akardı. Çerez `SameSite=Lax`: bankadan dönüş siteler arası bir üst seviye GET; `Strict` çerez bu istekte gönderilmez.
6. State tek kullanımlık; geri dönüşten sonra silinir.

Yenileme anahtarı tek kullanımlık (rotation). Yeni anahtar alınır alınmaz kaydediliyor, çünkü banka eskisini çoktan iptal etti.
Kilit yokken iki çalışma aynı anda yenilemeye kalkarsa, kaybeden veritabanını tekrar okuyup kazananın anahtarını kullanıyor.

Bir şirketin bir bankada tek canlı bağlantısı olabilir. Bunu veritabanı koruyor: `(CompanyId, Bank)` üzerinde
`Status <> 'Removed'` filtreli benzersiz indeks. Rıza iptal edilince yeniden bağlanmak aynı kaydı kullanıyor; hesaplar ve
geçmiş hareketler yerinde kalıyor.

## Sonuçları
- Veritabanı dökümü tek başına banka sırlarını vermiyor. Ama anahtar halkası da aynı veritabanında ve şifresiz; üretimde
  halka bir sertifika ya da anahtar kasasıyla (`ProtectKeysWithCertificate`, Azure Key Vault) sarılmalı. Kodda `shortcut:`
  notu var.
- Bağlantı kaldırılınca sırlar siliniyor, bankadaki rıza iptal ediliyor. Hareketler şirketin kaydı olduğu için kalıyor.
- Testli: akış uçtan uca, müşteri reddederse, başka tarayıcıdan geri dönüş, saklanan değerin şifreli olması, iptal edilen rıza.

## Mülakatta
"Açık bankacılıkta state ile PKCE'yi birlikte kullandım: state isteği tarayıcıya bağlıyor, PKCE kodu sunucuya. Geri dönüşte
oturum olmadığı için state'i ayrıca bir çerezle akışı başlatan tarayıcıya bağladım; yoksa saldırgan kendi bağlantısını
kurbana onaylatabilirdi. Sırlar Data Protection ile şifreli; anahtar halkasının üretimde ayrıca korunması gerektiğini biliyorum."
