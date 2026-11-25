# Hata senaryoları: ne olur, nasıl kanıtlandı

Plandaki (bölüm 9) 18 senaryonun her biri bir ya da daha çok otomatik testle kanıtlanıyor. Testler gerçek SQL Server,
Redis, MongoDB ve RabbitMQ'ya karşı çalışıyor (yerelde Docker, CI'da GitHub Actions servisleri). Bankalar ve ERP test
sunucuları; hata modları (`down`, `slow`, `baddata`, `loseresponse`) bu sunucuların `/mock` uçlarıyla açılıyor.

| # | Senaryo | Ne oluyor | Kanıt (test) |
|---|---|---|---|
| 1 | Aynı hareket iki kez gelir | `(hesap, banka referansı)` benzersiz indeksi (BIN2) tek kayıt bırakıyor; ikinci senkron 0 hareket alıyor | `KlasikBank_connects_imports_and_a_second_sync_imports_only_what_is_new` |
| 2 | Aynı bağlantı için iki senkron aynı anda | Redis kilidini biri alıyor, diğerleri 409 "zaten çekiliyor" | `Syncs_started_at_the_same_moment_store_each_movement_once` |
| 3 | Banka kapalı ya da yavaş | Senkron "başarısız", hata ve son senkron zamanı bağlantıda görünüyor, bağlantı etkin kalıyor, sonraki senkron telafi ediyor; 10 sn üstü zaman aşımı | `A_bank_that_is_down_records_the_error_and_the_next_sync_catches_up` (iki banka), `A_bank_slower_than_the_timeout_counts_as_down` |
| 4 | Banka anahtarının süresi dolmuş / rıza iptal | 401'de anahtar yenilenip tekrar deneniyor; rıza iptal edildiyse bağlantı "yeniden bağlanmalı", yeniden bağlanınca hesaplar korunuyor | `An_access_token_the_bank_no_longer_accepts_is_refreshed_and_the_sync_carries_on`, `A_consent_revoked_at_the_bank_asks_for_a_new_one_and_reconnecting_keeps_the_accounts` |
| 5 | Banka bozuk cevap döner | Hesabın cevabının tamamı reddediliyor, hiçbir satır yazılmıyor, ham cevap MongoDB'de duruyor, hata bağlantıda görünüyor | `An_answer_with_a_malformed_amount_is_rejected_whole_and_nothing_from_it_is_stored` (iki banka) |
| 6 | ERP kapalı | Mesajlar bekleme kuyruğunda dönüyor; ERP açılınca hepsi aktarılıyor | `While_the_ERP_is_down_exports_wait_and_go_through_when_it_is_back` |
| 7 | ERP fişi yazar ama cevap vermez | Aktarım servisi zaman aşımına düşüp tekrar deniyor; aynı `Idempotency-Key` ilk fişi döndürüyor, ikinci fiş yok | `When_the_ERP_writes_the_voucher_but_the_answer_is_lost_the_retry_makes_no_second_voucher`, `The_same_export_sent_twice_makes_one_voucher` |
| 8 | Bir mesaj sürekli hata verir | Deneme hakkı bitince ölü mektup kuyruğuna gidiyor; hareket "aktarılamadı", eşleşmeyenler ekranında ve panelde görünüyor; kişi yeniden gönderebiliyor. ERP'nin reddettiği fiş hiç tekrar denenmiyor | `After_every_retry_fails_the_export_is_dead_lettered_and_can_be_sent_again`, `A_voucher_the_ERP_refuses_fails_at_once_without_retrying` |
| 9 | Redis kapalı | Giriş, senkron (kilitsiz, çift kayıt yok), panel (önbelleksiz) çalışıyor; deneme sınırları süreç içinde sayılıyor; Redis koparken yapılan değişiklik paneli eskitmiyor; sağlık durumu "degraded" | `With_Redis_down_signing_in_still_works`, `With_Redis_down_the_limit_is_still_counted`, `A_change_made_while_Redis_is_away_still_retires_the_cached_dashboard`, `With_Redis_down_concurrent_syncs_still_store_each_movement_once`, `With_Redis_down_the_dashboard_is_computed_every_time`, `Api_without_Redis_and_MongoDB_is_degraded_not_down` |
| 10 | RabbitMQ kapalı | Outbox satırları veritabanında bekliyor; RabbitMQ'ya ulaşan bir API örneği gelince gönderiliyor ve aktarılıyor. Aktarım servisi kapalıyken de mesajlar kuyrukta bekliyor | `While_RabbitMQ_is_down_the_outbox_keeps_the_messages_and_sends_them_later`, `While_the_exporter_is_not_running_exports_wait_in_the_queue` |
| 11 | Başka şirketin kaydının ID'si tahmin edilir | 404 (var olduğu bile söylenmiyor); sorgu filtresini unutan kod da başka şirketi görmüyor ve yazamıyor | `Another_company_sees_none_of_the_bank_data_and_cannot_act_on_the_connection`, `Another_company_cannot_see_current_accounts_or_match_movements`, `Code_that_forgets_the_company_still_sees_nothing_and_cannot_write_across_companies` |
| 12 | İzleyici eşleme yapmaya çalışır | 403 | `Viewers_cannot_import_or_match_and_only_admins_set_the_ERP_firm`, `Viewers_cannot_connect_or_sync_accountants_can_sync_only_admins_remove` |
| 13 | Çalınan yenileme anahtarı tekrar kullanılır | Bütün oturum ailesi iptal | `A_refresh_token_works_once_and_a_replayed_one_signs_the_whole_session_out` |
| 14 | Çok fazla yanlış giriş | 5 hatada hesap 15 dk kilitli (yanlış şifreyle aynı 401, hesabın varlığı belli olmuyor); adres başına dakikada 10 deneme (429) | `Five_wrong_passwords_lock_the_account_even_for_the_right_password`, `Too_many_sign_in_attempts_from_one_address_are_refused_for_a_while` |
| 15 | Hareket açıklamasında "önceki talimatları unut" | Açıklama araç sonucunda veri olarak gidiyor; araçlar sadece okuyor ve sadece kullanıcının şirketini görüyor | `Tools_only_see_the_signed_in_company_even_when_a_movement_tells_them_otherwise` |
| 16 | 500 bin hareket | Ölçümler [olcumler.md](olcumler.md) dosyasında | `Half_a_million_movements` (`KASA_PERF=1` ile) |
| 17 | Tutar biçimi farklı gelir | Her banka kendi katı kalıbıyla okunuyor ("1.250,50" doğru okunuyor); kalıba uymayan ("1250.50", "12,5O") reddediliyor | `KlasikBank_connects_imports_...` (1.250,50 → 1250.50), `An_answer_with_a_malformed_amount_...` |
| 18 | İki muhasebeci aynı hareketi eşler | `rowversion`: ilki kazanıyor, ikincisi 409 "siz bakarken değişti"; aktarılmış hareketin eşleşmesi değiştirilemiyor | `Two_people_deciding_the_same_movement_the_second_is_told_it_changed` |

## Elle denenenler

- **İzleme:** `docker compose up -d` ile Aspire paneli (http://localhost:18888) açıldı, dört servis `dotnet run` ile çalıştırıldı,
  bir KlasikBank bağlantısı yapıldı. Tek istek için tek bir iz çıktı: 4 kaynak (API, test bankası, aktarım servisi, test ERP'si),
  derinlik 7. İz outbox satırında (`TraceParent`) saklanıp yayında devam ettirildiği ve RabbitMQ.Client 7 izi mesaj başlığında
  taşıdığı için kuyruğun iki yanı aynı izde.
- **Tarayıcı:** kayıt, ERP firma kodu ve cari alma, AcikBank onayı (bankanın sayfası dahil), KlasikBank bağlama, elle eşleme ve
  panelin sayfa yenilenmeden güncellenmesi denendi.
- **Akış:** Next.js geçişinin SSE'yi tamponlamadığı, bir saniye arayla gönderilen olayların bir saniye arayla geldiği ölçüldü.

## Bilinen sınırlar

- Banka çağrılarında otomatik yeniden deneme yok; bir sonraki senkron (15 dk) telafi ediyor.
- Outbox'a yazılmış ama RabbitMQ'ya hiç ulaşamayan mesajlar için bir uyarı yok; sağlık durumunda RabbitMQ "degraded" görünüyor.
- Gönderilmiş outbox satırları silinmiyor.
