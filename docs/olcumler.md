# Ölçümler

## 500 bin hareket (2026-11-19)

Tek şirket, 3 hesap, iki yıla yayılmış 500.000 hareket (üçte biri eşleşmiş). Yerel makine: Apple Silicon dizüstü, 8 GB RAM,
SQL Server 2022 Docker'da (Rosetta altında, 1,5 GB bellek sınırı). API süreç içinde (WebApplicationFactory), yani ağ yok;
süreler HTTP işleme, sorgu ve JSON dahil. Her ölçüm 1 ısınma + 7 çalıştırmanın ortancası.

Tekrar üretmek için:

```bash
KASA_PERF=1 dotnet test --filter "FullyQualifiedName~PerformanceTests" --logger "console;verbosity=detailed"
```

| Ölçüm | Ortanca (ms) |
|---|---|
| Hareketler, ilk sayfa (50) | 4,9 |
| Hareketler, 250.000. satırdan sonraki sayfa (keyset) | 6,1 |
| Aynı sayfa OFFSET ile (yalnız SQL) | 55,1 |
| Aynı sayfa keyset ile (yalnız SQL) | 1,8 |
| Eşleşmeyenler filtresi | 4,5 |
| Metin araması "ABC" (LIKE) | 5,9 |
| Panel, önbelleksiz | 253,3 |
| Panel, önbellekten (Redis) | 1,2 |
| Asistan aracı: bir yılın arama toplamları | 47,3 |
| 500.000 satırın yüklenmesi (SqlBulkCopy) | 17.300 |

## Ne anlama geliyor

- **Keyset sayfalama:** 250 bininci satırdaki sayfa, ilk sayfa kadar hızlı. OFFSET aynı sayfa için 30 kat yavaş ve sayfa
  ilerledikçe daha da yavaşlıyor, çünkü atladığı satırları okuyor. İndeks `(CompanyId, BookedAt, Id)`.
- **Panel önbelleği:** Önbelleksiz panel 253 ms. Zamanın çoğu, bütün hareketler üzerinde eşleşme ve aktarım sayımları ile
  30 günün Türkiye saatine göre gruplanması. Önbellekten 1,2 ms; yani her canlı güncellemede açık olan her ekran veritabanına
  gitmiyor. Önbellek sürüm anahtarıyla geçersiz kılındığı için eski veri gösterilmiyor (karar 0012).
- **Metin araması:** "ABC" sık geçtiği için ilk 50 sonuç hızlı bulunuyor. Nadir bir kelimede `LIKE '%...%'` bütün şirket
  hareketlerini tarar; bu boyutta kabul edilebilir, daha büyüğünde tam metin araması gerekir (kodda `shortcut:` notu var).

## İlk ölçüm (60 hareket, Sprint 4)

Panel önbelleksiz 28 ms, önbellekten 2 ms.
