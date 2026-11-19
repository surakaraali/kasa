# 0011: Web arayüzü: oturum, API'ye geçiş ve canlı güncelleme

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Arayüz Next.js ile yazıldı. Üç soru vardı: erişim anahtarı tarayıcıda nerede duracak, tarayıcı API'ye nasıl ulaşacak
(yenileme çerezi `SameSite=Strict` ve sadece `/api/auth` yolunda), ekran bir değişikliği nasıl hemen görecek?

## Seçenekler
1. **Anahtar localStorage'da, API ayrı adreste, CORS açık, ekran birkaç saniyede bir sorgular.** En kolayı. Ama sayfadaki
   herhangi bir betik (XSS) anahtarı okur; siteler arası çerez için `SameSite=None` gerekir; sorgulama boşa yük.
2. **Anahtar bellekte, API aynı adresin arkasında, değişiklikler SignalR ile itilir.**

## Karar
**2. seçenek.**
- **Aynı adres:** Next.js `/api/*` ve `/hubs/*` isteklerini `Kasa.Api`'ye aktarır (rewrites). Tarayıcı tek bir adres görür;
  yenileme çerezi aynı site çerezi olarak çalışır; API'de CORS yok.
- **Erişim anahtarı (15 dk) sadece bellekte.** Sayfa yenilenince httpOnly çerezle yeni anahtar alınır. Betikler çerezi
  okuyamaz; anahtar localStorage'da değil.
- **Tek yenileme:** Yenileme anahtarı tek kullanımlık ve ikinci kullanım bütün oturumları kapatıyor (çalınma tespiti, karar
  0003). Aynı anda iki istek 401 alırsa ikisi birden yenilemeye kalkardı; React geliştirme modunda efektler zaten iki kez
  çalışıyor. Bu yüzden sayfa genelinde tek bir yenileme isteği paylaşılıyor.
- **Canlı güncelleme:** SignalR hub'ı (`/hubs/kasa`). Bağlanan kullanıcı, anahtarındaki şirketin grubuna girer; şirket hiçbir
  zaman istemciden alınmaz. Senkron, eşleşme ve ERP sonucu olunca "changed" mesajı gider, ekran ilgili veriyi yeniden ister.
  Mesaj veri taşımıyor, sadece "değişti" diyor: yetki kontrolü yine normal API isteğinde yapılıyor. Bağlantı koptuktan sonra
  dönünce ekran bir kez yeniden çekiyor; kopukken kaçan mesajlar böyle telafi ediliyor.
- WebSocket kurulamazsa SignalR kendiliğinden SSE'ye ya da uzun sorgulamaya geçiyor; ara katman sorun olmuyor.

## Sonuçları
- Arayüz, API'nin verdiği hata metinlerini (problem details) olduğu gibi gösteriyor; Türkçe mesajlar tek yerde, API'de.
- shortcut: SignalR grupları tek süreçte. Birden çok API örneğinde Redis backplane (`AddStackExchangeRedis`) eklenmeli.
- Sunucu tarafında çizilen (SSR) sayfa yok; hepsi oturum açmış kullanıcıya özel, statik kabuk ve istemci tarafı veri.

## Mülakatta
"Erişim anahtarını bellekte, yenileme anahtarını httpOnly çerezde tuttum. API'yi Next'in arkasına aldım, böylece çerez aynı site
çerezi ve CORS gerekmiyor. Yenileme anahtarı tek kullanımlık olduğu için aynı anda iki yenileme isteği bütün oturumları
kapatırdı; bunu tek paylaşılan istekle çözdüm. Canlı güncellemede SignalR sadece 'değişti' diyor, veri yine yetkili API'den geliyor."
