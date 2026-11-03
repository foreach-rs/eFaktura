# ForEach.Infrastructure.External.eFaktura

Everything that knows the Serbian e-invoicing system — the SEF public API and the shape of a
Serbian UBL 2.1 invoice — in one library with no dependencies, so more than one product can file
invoices with the tax authority.

## What it does not do

- **It computes no money.** The UBL writer formats and validates; every amount arrives already
  calculated. A library that rounded VAT would be rounding it differently from the invoice the
  customer was shown.
- **It persists nothing and retries nothing.** A result says whether retrying is safe; when to do
  it belongs to somebody's job scheduler.
- **It decides no policy.** Which buyers may be filed, which invoice numbers are used, when a
  storno is allowed — all of that is the host's.

## The three outcomes

```csharp
var result = await client.SendSalesInvoiceUblAsync(ubl, requestId, cir);

if (result.IsSuccess) return result.Value;
if (result.Outcome == SefOutcome.Unknown) return Reconcile(requestId);  // never resend
if (result.IsWorthRetrying) return Retry(result);
Show(result.FailureMessage);
```

`Unknown` is the one that matters. Filing an invoice is not idempotent, so a timeout on a send is
not a failure: the document may be at the tax authority. Sending it again would file a second
invoice under the same number, and unpicking that is a storno and an explanation rather than a bug
fix. Every other failure — including a timeout on a read — is a plain `Failed`.

`SefError.Kind` sorts the refusals into the three responses a host has:

| Kind | HTTP | What to do |
|---|---|---|
| `Configuration` | 401, 403 | the key is wrong, expired, or from the other environment — a person must act |
| `CallerMistake` | 400, 409, 422 | invalid UBL, or an action the invoice's status forbids — fix and resend |
| `Transient` | 408, 429, 5xx, anything unknown | retry later |

`ResponseBody` is kept on every outcome, success included, because a host stores it: when an
accountant asks in six months why an invoice was rejected, the answer has to be what SEF said.

## Credentials

`SefCredentials` carries the address and the key together, because they belong together — a demo
key against production is refused with the same 401 as a revoked one. `IsProduction` answers
whether a call is about to file something with the state, which is worth asking before offering
anybody a "send a test invoice" button.

## Testing

`…eFaktura.UnitTests` covers the result model, the readers, the request shape and the UBL writer
against recorded documents, without a network. There is no live suite yet: when one is written it
should run against the demo system only and skip itself unless its credentials are set, so it
never runs in CI by accident — and never touches production, which has no such switch by design.
