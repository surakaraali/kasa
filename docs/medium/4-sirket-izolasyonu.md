# Ortak veritabanında bir şirketin verisini diğerinden ayırmak

Kasa'yı birden çok şirket kullanıyor ve hepsinin verisi aynı SQL Server veritabanında duruyor. `BankTransactions` tablosunda
iki şirketin hareketleri yan yana. Bankalar bu projede test sunucuları, ama kuralı gerçek para varmış gibi kurdum.

Bu düzende en ciddi hata küçük bir unutkanlıktan çıkar. Yeni bir uç nokta yazarım, sorguya `CompanyId` koşulunu eklemeyi
unuturum ve bir muhasebeci başka şirketin banka hareketini görür. Bir başkası da URL'deki id'yi değiştirip şansını dener.
Bu yüzden izolasyonu benim her sorguda hatırlamama bağlamak istemedim.

Şirket başına ayrı veritabanı en güçlü seçenekti. Ama göçler, bağlantılar ve maliyet şirket sayısıyla artıyor. Ortak
veritabanında kaldım ve dört kural koydum.

## Şirket kimliği yalnızca token'dan geliyor

Geçerli şirketi `TenantContext` söylüyor:

```csharp
public Guid? CompanyId =>
    _override ?? (Guid.TryParse(http.HttpContext?.User.FindFirstValue(KasaClaims.CompanyId), out var id) ? id : null);

public void Use(Guid companyId) => _override = companyId;
```

Değer, imzalı JWT'deki `company_id` alanından okunuyor. İstek gövdesinde ya da URL'de şirket alanı yok. `Use` metodunu oturumu
olmayan dört akış çağırıyor: zamanlanmış senkron, ERP sonucunu işleyen döngü, davet kabulü ve banka geri dönüşü.

`TenantContext` istek başına bir örnek olarak kayıtlı. Zamanlanmış senkron da her bağlantı için yeni bir kapsam açıp şirketi
orada seçiyor. Böylece bir işin seçtiği şirket diğerine taşınmıyor.

## Okuma tarafı: sorgu filtresi

EF Core'da filtre genelde tablo tablo `HasQueryFilter` ile yazılır. Kasa'da bu çağrı yok. `OnModelCreating` sonunda modeldeki
varlıkları dönüyorum. `ITenantEntity` uygulayan her varlık için ifade ağacını kurup filtreyi metadata üzerinden ekliyorum:

`type.SetQueryFilter(Expression.Lambda(Expression.Equal(companyId, current), row));`

Böylece yeni bir tablo arayüzü uyguladığı anda filtreyi de alıyor. Bugün altı tablo bu durumda. Filtre, context üzerindeki
`CurrentCompanyId` özelliğini okuyor. EF Core modeli bir kez kurup sakladığı için içine sabit bir id yazamazdım. Özellik her
sorguda yeniden okunuyor, tek model bütün şirketlere hizmet ediyor.

Şirket bilinmiyorsa değer `null` oluyor ve filtre hiçbir satır döndürmüyor. Varsayılan davranış "hiçbir şey gösterme".

## Yazma tarafı: SaveChanges koruması

Filtre okumayı sınırlıyor. Elle oluşturulmuş bir satırın `Add` ile eklenmesini engellemiyor. Bunun için `SaveChanges` önünde
bir kontrol çalışıyor:

```csharp
foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
{
    // ...
    var current = tenant.CompanyId
        ?? throw new InvalidOperationException($"Cannot save {entry.Entity.GetType().Name}: no company is set for this work.");

    if (entry.State == EntityState.Added && entry.Entity.CompanyId == Guid.Empty)
    {
        entry.Entity.CompanyId = current;
    }

    if (entry.Entity.CompanyId != current)
    {
        throw new InvalidOperationException(
            $"Cannot save {entry.Entity.GetType().Name} of company {entry.Entity.CompanyId} while working for company {current}.");
    }
}
```

Şirketi boş gelen yeni satır geçerli şirketi alıyor. Başka şirketin değerini taşıyan satır veritabanına gitmeden hata veriyor.
`Code_that_forgets_the_company_still_sees_nothing_and_cannot_write_across_companies` testi iki tarafı da deniyor. Şirketsiz
açılan context, satırlar varken sıfır sonuç alıyor. B şirketi adına A şirketine satır ekleme denemesi hata veriyor.

## Neden 404

Başka şirketin kaydına 403 dönseydim, o id'de bir kayıt olduğunu söylemiş olurdum. Id deneyen biri için bu da bilgidir. Kasa'da
404 için ayrı bir kontrol yazmadım. Filtre satırı gizlediği için sorgu boş dönüyor ve uç nokta `NotFound` veriyor.
`Another_company_sees_none_of_the_bank_data_and_cannot_act_on_the_connection` testi senkron ve silme denemelerinde 404 bekliyor.
Eşleme ve hareket geçmişi için aynı kontrolü `Another_company_cannot_see_current_accounts_or_match_movements` yapıyor.

403'ü kendi şirketindeki yetki için kullanıyorum. İzleyici rolündeki kullanıcı senkron başlatmak isterse 403 alıyor. Bunu da
`Viewers_cannot_connect_or_sync_accountants_can_sync_only_admins_remove` testi kontrol ediyor.

## Bu modelin sınırları

- Kullanıcı tablosunda filtre yok, çünkü giriş anında şirket henüz bilinmiyor. Ekip ekranlarında `u.CompanyId == companyId`
  koşulunu elle yazıyorum. Unutma riski bu tabloda duruyor.
- Kodda dört yerde `IgnoreQueryFilters()` var. Hepsi şirketin oturumdan gelmediği akışlarda ve kolayca aranabiliyor.
- `SaveChanges` koruması `ExecuteDeleteAsync` gibi toplu komutları görmüyor. Davet iptali böyle çalışıyor ve onu yalnız sorgu
  filtresi sınırlıyor. Başka şirketin bu ucu denemesi için ayrı bir testim yok.
- Koruma satırın bellekteki `CompanyId` değerine bakıyor. Veritabanından okunan ilk değerle karşılaştırma yapmıyor.
- Filtre her sorguya bir şart ekliyor. Bu yüzden `CompanyId` sütunlarında indeks gerekiyor.
- Bir şirketin verisini topluca dışarı almak ya da silmek, ayrı veritabanına göre daha zor.
