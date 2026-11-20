-- Kasa demo veritabanında gösterilecek sorgular.
-- Hepsi:            scripts/db.sh -f docs/demo-sorgulari.sql
-- Tek tek:          scripts/db.sh   (sorguyu yapıştır, alt satıra GO yaz, Enter; çıkmak için :quit)
-- Burada "sa" ile bağlanıyoruz, uygulamanın şirket filtresi yok; o yüzden şirketi elle seçiyoruz.
-- Uygulamada bu koşulu EF Core'un global sorgu filtresi her sorguya kendisi ekliyor.
SET NOCOUNT ON;
DECLARE @sirket uniqueidentifier = (SELECT Id FROM Companies WHERE Name = N'Anadolu Tekstil A.Ş.');

PRINT N'1) Tablolar ve satır sayıları';
SELECT CAST(t.name AS nvarchar(24)) AS Tablo, SUM(p.rows) AS Satir
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY t.name ORDER BY t.name;

PRINT N'2) Son 10 hareket: hangi hesaptan, kimden, hangi cariye, hangi fişle';
SELECT TOP (10) CONVERT(char(16), h.BookedAt, 120) AS Tarih, CAST(a.Name AS nvarchar(16)) AS Hesap, CAST(h.CounterpartyName AS nvarchar(26)) AS KarsiTaraf,
       h.Amount AS Tutar, CAST(h.MatchStatus AS varchar(10)) AS Eslesme, CAST(h.MatchRule AS varchar(10)) AS Kural,
       CAST(c.Title AS nvarchar(26)) AS Cari, CAST(h.ErpVoucherNo AS varchar(10)) AS Fis
FROM BankTransactions h
JOIN BankAccounts a ON a.Id = h.BankAccountId
LEFT JOIN CurrentAccounts c ON c.Id = h.CurrentAccountId
WHERE h.CompanyId = @sirket
ORDER BY h.BookedAt DESC, h.Id DESC;

PRINT N'3) Kararınızı bekleyenler: en çok kimden';
SELECT CAST(COALESCE(h.CounterpartyName, N'(karşı taraf yok)') AS nvarchar(26)) AS KarsiTaraf, COUNT(*) AS Adet, CAST(SUM(h.Amount) AS decimal(14,2)) AS Toplam
FROM BankTransactions h
WHERE h.CompanyId = @sirket AND h.MatchStatus = 'Unmatched'
GROUP BY h.CounterpartyName ORDER BY Adet DESC;

PRINT N'4) Eşleşme kararlarının kaydı: kim, ne zaman, hangi kuralla';
SELECT TOP (10) CONVERT(char(16), m.[At], 120) AS Zaman, CONVERT(char(10), h.BookedAt, 104) AS Hareket, CAST(m.Status AS varchar(10)) AS Karar, CAST(m.[Rule] AS varchar(10)) AS Kural,
       CAST(COALESCE(u.FullName, N'(otomatik)') AS nvarchar(14)) AS Kim, CAST(c.Title AS nvarchar(26)) AS Cari, h.Amount AS Tutar
FROM MatchAudits m
JOIN BankTransactions h ON h.Id = m.BankTransactionId
LEFT JOIN AspNetUsers u ON u.Id = m.UserId
LEFT JOIN CurrentAccounts c ON c.Id = m.CurrentAccountId
WHERE m.CompanyId = @sirket
ORDER BY m.[At] DESC;

PRINT N'5) Outbox: ERP mesajları eşleşmeyle aynı işlemde yazılıyor, sonra RabbitMQ''ya gönderiliyor';
SELECT TOP (10) CONVERT(char(19), o.CreatedAt, 120) AS Yazildi, CONVERT(char(19), o.PublishedAt, 120) AS Gonderildi,
       CAST(o.Type AS varchar(14)) AS Tur, o.Attempts AS Deneme, CAST(o.TraceParent AS varchar(30)) AS Iz
FROM OutboxMessages o
WHERE o.CompanyId = @sirket
ORDER BY o.CreatedAt DESC;

PRINT N'6) Tek kayıt garantisi: aynı hesapta aynı banka referansı iki kez olamaz (ikili karşılaştırma)';
SELECT CAST(i.name AS varchar(48)) AS Indeks, i.is_unique AS Tekil,
       (SELECT CAST(collation_name AS varchar(24)) FROM sys.columns WHERE object_id = OBJECT_ID('BankTransactions') AND name = 'BankReference') AS Karsilastirma
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('BankTransactions') AND i.name = 'IX_BankTransactions_BankAccountId_BankReference';

PRINT N'7) Şirket ayrımı: CompanyId taşıyan tablolar';
SELECT CAST(t.name AS nvarchar(24)) AS Tablo
FROM sys.columns col JOIN sys.tables t ON t.object_id = col.object_id
WHERE col.name = 'CompanyId' ORDER BY t.name;

PRINT N'8) Aylık hesap özeti: giren, çıkan, net';
SELECT CAST(FORMAT(h.BookedAt, 'yyyy-MM') AS char(7)) AS Ay,
       CAST(SUM(CASE WHEN h.Amount > 0 THEN h.Amount ELSE 0 END) AS decimal(14,2)) AS Giren,
       CAST(SUM(CASE WHEN h.Amount < 0 THEN -h.Amount ELSE 0 END) AS decimal(14,2)) AS Cikan,
       CAST(SUM(h.Amount) AS decimal(14,2)) AS Net
FROM BankTransactions h
WHERE h.CompanyId = @sirket
GROUP BY FORMAT(h.BookedAt, 'yyyy-MM') ORDER BY Ay;
