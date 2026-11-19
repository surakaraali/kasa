# Yapay zekâ ile geliştirme: bu proje nasıl yapıldı

Kasa, Claude Code (Anthropic'in kodlama ajanı) ile birlikte geliştirildi. Bu dosya işin nasıl bölündüğünü ve sonucun neden
güvenilir olduğunu anlatıyor. Kısaca: yapay zekâ hızlı yazdı; neyin yapılacağına, neyin yeterince iyi olduğuna ben karar verdim
ve her iddia bir testle ya da ölçümle kanıtlandı.

## İşin bölünmesi

| Ben | Claude Code |
|---|---|
| Problemi ve kapsamı seçtim (iş ilanındaki teknolojiler, nakit yönetimi) | Planı, sprintleri, veri modelini ve hata senaryoları tablosunu önerdi |
| Planı okuyup onayladım; adı, demo yerini, depo görünürlüğünü, maliyet sınırını (asistanda ucuz model) ben belirledim | Onaylanan plana göre kodu, testleri ve karar notlarını yazdı |
| Her sprint sonunda soruları çalıştım ([sorular.md](sorular.md)) | Her sprint için "mülakatta sorulursa" soruları ve ipuçlarını hazırladı |
| Sonuçları gözden geçirdim, yön değiştirmek istediğimde durdurup yeniden yönlendirdim | Testleri çalıştırdı, ekranları tarayıcıda denedi, CI'ı yeşil tutmadan ilerlemedi |
| Anahtarları ve şifreleri kendim girdim | Hiçbir gerçek anahtar ya da şifre girmedi; sadece test değerleri |

## Güveni nereden alıyorum

- **Testler gerçek altyapıya karşı:** 77 entegrasyon testi gerçek SQL Server, Redis, MongoDB ve RabbitMQ ile çalışıyor; sahte
  (mock) veritabanı yok. Bankalar, ERP ve Claude test sunucularıyla taklit ediliyor; bunların hata modları var.
- **Hata senaryoları önce yazıldı:** "Bu servis çökerse ne olur?" sorusunun 18 cevabı kod yazılmadan önce tabloya kondu, sonra her biri
  için test yazıldı ([hata-senaryolari.md](hata-senaryolari.md)).
- **Kararlar yazılı:** 14 karar notu; her birinde seçenekler, seçilen ve bedeli ([kararlar](kararlar)). Bilerek bırakılan
  kısayollar kodda `shortcut:` notuyla işaretli ve [guvenlik.md](guvenlik.md) içinde "eksik" olarak listeli.
- **Ölçüm, iddia değil:** önbellek ve sayfalama kararları 500 bin hareketle ölçüldü ([olcumler.md](olcumler.md)).
- **Agile pano:** her sprint bir kilometre taşı, her iş bir kart; kartlar commit'lerle kapanıyor (GitHub Projects).

## Yapay zekânın yanıldığı ve düzeltildiği yerler

- Asistanın varsayılan modeli bir ara pahalı modele ayarlanmıştı; plandaki maliyet kararına (Haiku) geri döndürüldü.
- Panel grafiği her canlı güncellemede çubukları sıfırdan büyütüyordu; tarayıcıda denerken fark edildi, animasyon kapatıldı.
- Outbox'un saniyede bir yaptığı sorgu izleme panelini dolduruyordu; bu sorgu izlemeden çıkarıldı.

## Öğrendiklerim için

Her sprintin sonunda, o sprintte kullanılan her teknik karar için bir soru ve kısa bir ipucu var. Amaç kodu ezberlemek değil,
"neden böyle?" sorusuna kendi cümlelerimle cevap verebilmek.
