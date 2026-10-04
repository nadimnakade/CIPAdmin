# Bank transaction classification regression checks

Run from the CIPAdmin directory:

```bash
dotnet run --project tools-bsatest/BsaRegression.csproj -c Release -- tools-bsatest/statement-check.pdf
```

Uses the existing backend libraries; no new test packages. The included statements are synthetic, not customer data. The PDF fixture is a two-row text-based Debit/Credit/Balance table. Stop a locally running backend using the same Release binaries before rebuilding on Windows.

Coverage includes salary credits (not generic NACH credits), manual/auto-debit EMI payments, insurance, mutual funds/SIP, deposits, bonds, NPS/PPF, credit-card bills, equity purchases/sales, F&O, ambiguous broker funding/payouts, ordinary merchant payments, same-day EMI rows, debit/credit conservation, source line references, CSV/XLSX/TXT/PDF parsing, both existing multipart upload controllers and Excel generation.

Both analyzer screens expose a Transactions tab and stock-market Segment/Activity/Review columns. Excel uses the same table definitions. Existing `/api/bsa/analyze`, `/api/bsa/analyze-flex` and `/api/bsa/export` interfaces remain; response fields are additive. Historical results need re-analysis to populate new fields.

## Limits

- Classifications are narration-based rules, not verified trades or obligations. Unknown entries and ambiguous broker transfers require review.
- No trade quantities, security symbols, holdings, buy/sell execution confirmation or trading profit is inferred from a broker funding/payout alone. Use contract notes or broker ledgers.
- Unlabelled salary credits remain Other rather than being guessed from amount or payment channel. Recognized lender auto-debits without explicit repayment wording are marked for review.
- Insurance premiums do not establish policy cover, surrender value or whether the policy is investment-linked.
- Tests cover synthetic layouts, not every bank PDF. Scanned/image-only PDFs still require a text-based export or CSV/XLSX.
- Existing loan-summary/underwriting formulas were not redesigned; loan-type defaults and same-lender loan aggregation still need independent validation for underwriting use.
