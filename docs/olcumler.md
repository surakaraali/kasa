# Ölçümler

## 500 bin hareket (2026-11-23)

Tek şirket, 3 hesap, iki yıla yayılmış 500.000 hareket (üçte biri eşleşmiş). Yerel makine: Apple Silicon dizüstü, 8 GB RAM,
SQL Server 2022 Docker'da (Rosetta altında, 1,5 GB bellek sınırı). API süreç içinde (WebApplicationFactory), yani ağ yok;
süreler HTTP işleme, sorgu ve JSON dahil. Her ölçüm 1 ısınma + 7 çalıştırmanın ortancası.

Tekrar üretmek için:

```bash
KASA_PERF=1 dotnet test --filter "FullyQualifiedName~PerformanceTests" --logger "console;verbosity=detailed"
```

| Ölçüm | Ortanca (ms) |
|---|---|
| Hareketler, ilk sayfa (50) | 3,3 |
| Hareketler, 250.000. satırdan sonraki sayfa (keyset) | 3,4 |
| Aynı sayfa OFFSET ile (yalnız SQL) | 46,9 |
| Aynı sayfa keyset ile (yalnız SQL) | 1,3 |
| Eşleşmeyenler filtresi | 3,4 |
| Metin araması "ABC" (LIKE) | 4,6 |
| Panel, önbelleksiz | 324,6 |
| Panel, önbellekten (SQL'den sürüm + Redis) | 2,9 |
| Asistan aracı: bir yılın arama toplamları | 45,3 |
| 500.000 satırın yüklenmesi (SqlBulkCopy) | 19.200 |

## Ne anlama geliyor

- **Keyset sayfalama:** 250 bininci satırdaki sayfa, ilk sayfa kadar hızlı. OFFSET aynı sayfa için yaklaşık 36 kat yavaş ve sayfa
  ilerledikçe daha da yavaşlıyor, çünkü atladığı satırları okuyor. İndeks `(CompanyId, BookedAt, Id)`.
- **Panel önbelleği:** Önbelleksiz panel 325 ms. Zamanın çoğu, bütün hareketler üzerinde eşleşme ve aktarım sayımları ile
  30 günün Türkiye saatine göre gruplanması. Önbellekten 2,9 ms; yani her canlı güncellemede açık olan her ekran veritabanına
  gitmiyor. Sürüm SQL'de tutulduğu için Redis koparken yapılan değişiklik de önbelleği geçersiz kılıyor (karar 0012).
- **Metin araması:** "ABC" sık geçtiği için ilk 50 sonuç hızlı bulunuyor. Nadir bir kelimede `LIKE '%...%'` bütün şirket
  hareketlerini tarar; bu boyutta kabul edilebilir, daha büyüğünde tam metin araması gerekir (kodda `shortcut:` notu var).

## İlk ölçüm (60 hareket, Sprint 4)

Panel önbelleksiz 28 ms, önbellekten 2 ms.
