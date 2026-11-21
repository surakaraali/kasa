#!/usr/bin/env bash
# Live moments for the demo, played on the test bank and the test ERP (reachable from this computer only):
#   scripts/demo-olay.sh tahsilat    ABC Tekstil pays 7.500 TL into KlasikBank; press "Şimdi çek" and it matches by IBAN
#   scripts/demo-olay.sh erp-kapat   the test ERP stops answering; a matched movement waits, retried every 10 s, 5 times
#   scripts/demo-olay.sh erp-ac      the ERP is back; the waiting voucher is written, once
set -euo pipefail
post() { curl -sf -o /dev/null -X POST "$1" -H 'content-type: application/json' -d "$2"; }
case "${1:-}" in
  tahsilat)
    post http://localhost:5102/mock/klasikbank/movements \
      '{"amount":7500.00,"description":"FATURA TAHSILATI","counterpartyName":"ABC TEKSTİL SANAYİ A.Ş.","counterpartyIban":"TR340004605555000011112222"}'
    echo "KlasikBank'a ABC Tekstil'den 7.500,00 TL geldi. Kasa'da Banka bağlantıları > KlasikBank > \"Şimdi çek\"." ;;
  erp-kapat)
    post http://localhost:5103/mock/fault '{"mode":"Down"}'
    echo "Test ERP kapalı. Şimdi bir hareket eşleyin; fiş 10 saniye arayla denenecek. 40 saniye içinde: scripts/demo-olay.sh erp-ac" ;;
  erp-ac)
    post http://localhost:5103/mock/fault '{"mode":"None"}'
    echo "Test ERP açık. Bekleyen fiş bir sonraki denemede yazılır (en geç 10 saniye)." ;;
  *)
    echo "kullanım: scripts/demo-olay.sh [tahsilat|erp-kapat|erp-ac]"; exit 1 ;;
esac
