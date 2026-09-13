# Railway configuration

Modbot's Railway project is defined in `railway.ts` — **one service plus one Postgres**, per
foundation spec §2.5.

```bash
npm install railway     # the IaC SDK, needed to evaluate railway.ts
railway login && railway link
railway config plan     # preview
railway config apply    # apply after confirmation
```

## Why there is no `railway.json`

Railway deprecated Config as Code. **New services cannot opt into it**, and existing ones stop
honouring it on **2026-12-01**. A `railway.json` in this repository would therefore do nothing for
anyone deploying Modbot — and worse, Railway *blocks* IaC plans for a service still managed by Config
as Code, so shipping both would create the two-sources-of-truth problem the platform explicitly
refuses.

IaC also does the thing the JSON file could not: it declares the database **alongside** the service
and wires `DATABASE_URL` from it. A fresh install is one `apply`, rather than "provision a Postgres,
find its URL, paste it into the service".
