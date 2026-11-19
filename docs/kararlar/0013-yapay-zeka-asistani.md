# 0013: Yapay zekâ asistanı: sadece okuyan araçlar ve dar bir güven sınırı

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Muhasebe ekibi "bu ay ABC Tekstil'den ne kadar geldi?", "eşleşmeyenler en çok kimden?" gibi soruları ekranlarda gezinmeden
sormak istiyor. Bir dil modeli bunu yapabilir, ama üç risk var: modelin başka şirketin verisine ulaşması, banka açıklamalarına
gömülmüş talimatlara uyması (prompt injection) ve maliyetin kontrolsüz büyümesi.

## Seçenekler
1. **Veritabanına doğrudan SQL yazdırmak.** En esnek. Ama model herhangi bir sorgu yazabilir; şirket filtresini unutması
   yeter, yetki modelin elinde olur.
2. **Bütün veriyi isteğe koymak.** Basit, ama veri büyüyünce sığmaz ve gereğinden fazla veri dışarı çıkar.
3. **Az sayıda, sadece okuyan araç.** Model ne istediğini söyler; sorguyu Kasa yazar ve çalıştırır.

## Karar
**3. seçenek.** Beş araç: `list_accounts`, `search_transactions`, `cash_flow`, `current_account_summary`, `unmatched_overview`.
- **Şirket:** Araçlar şirket kimliği almıyor. Her sorgu şirket filtreli DbContext'ten geçiyor (karar 0004); şirket
  kullanıcının JWT'sinden geliyor. Model ne yazarsa yazsın başka şirkete ulaşamaz.
- **Yazma yok:** Hiçbir araç eşleme yapmaz, ERP'ye göndermez, ayar değiştirmez. Model istenirse kullanıcıyı ilgili ekrana yönlendirir.
- **Veri azaltma:** IBAN ve vergi numarası maskeleniyor (`TR09 **** **** 0001`, `12******90`); model tam numarayı hiç görmüyor.
  Satır sayısı en fazla 50; toplamlar ayrıca hesaplanıyor.
- **Prompt injection:** Açıklamalar ve karşı taraf adları bankadan ve parayı gönderenden geliyor, yani dış veri. Araç
  sonuçlarında JSON alanı olarak gidiyorlar. Sistem talimatı bunları veri olarak ele almasını söylüyor. Asıl koruma
  mimaride: talimata uysa bile yapabileceği tek şey kendi şirketinin verisini okumak.
- **Girdi doğrulama:** Araç girdileri akarken geliyor (`eager_input_streaming`); Kasa her alanı kendisi doğruluyor (tarih
  biçimi, aralıklar, uzunluk). Hatalı girdi modele `is_error` sonucu olarak dönüyor, istisna fırlatılmıyor.
- **Maliyet ve süre:** Soru başına en fazla 6 araç turu. Kullanıcı başına saatte 30 soru (Redis). Çağrı başına 90 saniye
  zaman aşımı. Geçmiş en fazla 20 mesaj, mesaj başına 4.000 karakter. Her turun token kullanımı loglanıyor.
- **Önbellek:** Sistem talimatı ve araç listesi sabit; tarih ve şirket adı son soruya ayrı bir blok olarak ekleniyor. Böylece
  önek her istekte aynı ve önbelleğe alınabiliyor. Araç turlarında konuşma da önbellekten okunuyor (üst düzey `cache_control`).
- **Model:** Varsayılan `claude-opus-5-5`, çaba (effort) `low`: sohbet ve basit araç kullanımı için yeterli ve hızlı.
  `Assistant:Model` ile değiştirilebilir (örneğin daha ucuz `claude-haiku-5-5`). Sunucu tarafı yedek model açık
  (`fallbacks: "default"`): bir güvenlik sınıflandırıcısı soruyu reddederse aynı çağrıda başka bir model cevaplıyor.
- **Anahtar yoksa** asistan 503 dönüyor, arayüz "kapalı" diyor; uygulamanın geri kalanı etkilenmiyor.
- **Akış:** Cevap SSE ile kelime kelime geliyor. Araç çalışırken arayüz "Hareketler aranıyor…" gibi bir adım gösteriyor.

## Sonuçları
- Testler sahte bir Claude sunucusuyla, gerçek akış biçiminde çalışıyor: araç turu, düşünme bloğunun imzasıyla geri
  gönderilmesi, maskeleme, hatalı girdi, tur sınırı, saatlik sınır, bozuk geçmiş. Gerçek modelle deneme API anahtarı
  eklenince yapılacak.
- Asistanın cevabı yanlış olabilir; her toplamın hangi dönemi ve filtreyi kapsadığını söylemesi isteniyor ki kullanıcı kontrol edebilsin.
- shortcut: konuşma sunucuda saklanmıyor, tarayıcı geçmişi her soruda gönderiyor. Uzun konuşmalar ya da denetim kaydı gerekirse sunucuda saklanmalı.

## Mülakatta
"Modele SQL yazdırmadım, sadece okuyan beş araç verdim. Şirket kimliğini araçlar almıyor, kullanıcının token'ından geliyor;
model ikna edilse bile başka şirkete ulaşamaz. Banka açıklamalarını dış veri sayıp araç sonucunda veri olarak gönderiyorum ve
IBAN, vergi numarası maskeli gidiyor. Maliyeti tur sınırı, saatlik soru sınırı ve zaman aşımıyla sınırladım; testler sahte bir
Claude sunucusuyla çalışıyor, yani API anahtarı olmadan da CI'da koşuyor."
