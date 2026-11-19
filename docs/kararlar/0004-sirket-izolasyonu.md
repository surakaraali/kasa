# 0004: Şirket izolasyonu: sorgu filtresi ve yazma koruması

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Kasa'yı birçok şirket kullanıyor ve hepsinin verisi aynı veritabanında. Bir şirketin başka bir şirketin banka
hareketini görmesi, en ciddi hata olur. Bu tür hata genelde bir geliştiricinin bir sorguda `where CompanyId = ...`
yazmayı unutmasıyla olur. Ya da bir id tahmin edilip URL'ye yazıldığında (IDOR).

## Seçenekler
1. **Her sorguda elle filtre.** Unutulmaya açık; tek bir unutulan sorgu veri sızdırır.
2. **Şirket başına ayrı veritabanı.** En güçlü izolasyon. Ama göçler, bağlantılar ve maliyet şirket sayısıyla artar.
3. **Ortak veritabanı + EF Core global sorgu filtresi + kayıtta koruma.**

## Karar
**3. seçenek.**
- **Okuma tarafı:** `ITenantEntity` olan her tablo kendiliğinden bir sorgu filtresi alıyor: `CompanyId == geçerli şirket`.
  Filtreyi yazmayı unutmak mümkün değil. Yeni bir tablo arayüzü uyguladığı anda o da korunuyor.
- **Şirket kimliği nereden geliyor:** sadece giriş yapmış kullanıcının token'ından. İstek gövdesinden ya da URL'den asla.
- **Şirket bilinmiyorsa:** filtre hiçbir şey döndürmüyor. Varsayılan "hiçbir şey göster", "her şeyi göster" değil.
- **Yazma tarafı:** `SaveChanges` başka bir şirketin satırını eklemeyi, değiştirmeyi ya da silmeyi reddediyor. Yeni satırlar
  geçerli şirketi kendiliğinden alıyor.
- **Başka şirketin kaydı 404 dönüyor, 403 değil.** Kaydın var olduğu bile belli olmuyor.
- **Bilinçli istisnalar:**
  - Kullanıcı tablosunda filtre yok, çünkü giriş yaparken şirket henüz bilinmiyor. Yönetici ekranları şirketi açıkça yazıyor.
  - Arka plan işleri ve davet kabulü gibi anonim akışlar şirketi `TenantContext.Use(...)` ile açıkça seçiyor.
  - Filtreyi kapatmak için `IgnoreQueryFilters()` gerekiyor. Kodda görünür ve aranabilir.

## Sonuçları
- Bir şirketin verisini topluca dışarı almak ya da silmek, ayrı veritabanına göre daha zor.
- Filtre her sorguya bir şart ekliyor. `CompanyId` sütunlarında index olması bu yüzden önemli.

## Mülakatta
"İzolasyonu her geliştiricinin hatırlamasına bırakmadım. EF Core'un global sorgu filtresi okumayı, SaveChanges'teki
kontrol yazmayı koruyor. Şirket kimliği sadece token'dan geliyor. Bilinmiyorsa hiçbir şey dönmüyor. Bunları test ettim:
başka şirketin id'si 404 veriyor, başka şirkete yazma denemesi hata veriyor."
