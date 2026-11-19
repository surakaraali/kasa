# 0002: .NET 10 (LTS) ve tek yerden sabitlenen paket sürümleri

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-19

## Bağlam
Kasım 2026 itibarıyla .NET 11 yayında, .NET 10 ise uzun süreli destekli (LTS) sürüm. NuGet'te "en son" sürüm artık
11.x paketleri. Projede altı farklı proje var; her birinin paket sürümünü ayrı ayrı yönetmek, sürümlerin zamanla
birbirinden kaymasına yol açar.

## Seçenekler
1. **.NET 11.** En yeni özellikler. Ama kısa süreli destek (STS); bir ürün ekibi genelde LTS üzerinde kalır.
2. **.NET 10 (LTS).** Kasım 2028'e kadar destekli. Önceki projelerim de bu sürümde.

Paket sürümleri için:
1. Her projenin kendi `.csproj` dosyasında sürüm.
2. **Merkezi paket yönetimi:** bütün sürümler `Directory.Packages.props` içinde, bir kez.

## Karar
- `.NET 10`, `global.json` ile sabit (10.0.404, yalnızca yama güncellemeleri).
- Microsoft paketleri 10.0.x serisinde. "En son" 11.x'e kendiliğinden geçilmiyor.
- **Merkezi paket yönetimi** açık. Bir projede `Version=` yazılırsa derleme hata veriyor.
- Ortak ayarlar `Directory.Build.props` dosyasında: nullable açık, **uyarılar hata sayılır**.

## Sonuçları
- Bir paketi yükseltmek tek satır. Bütün projeler aynı sürümü kullanıyor.
- Uyarıların hata sayılması küçük sorunların birikmesini engelliyor, ama yeni bir paket uyarı üretirse derleme durur.
  Bu bilinçli bir tercih.

## Mülakatta
"LTS sürümünde kaldım, çünkü bir ürün ekibi genelde öyle yapar. Paket sürümlerini tek dosyada topladım; altı proje hep
aynı sürümle derleniyor, uyarılar da hata sayılıyor."
