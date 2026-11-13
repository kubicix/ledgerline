# Spec: Notifications

Project `src/Services/Notifications/Ledgerline.Notifications`. Database
`notifications_db`. Shared conventions: `building-blocks.md`.

## 1. Responsibilities

- Turn payment events into emails to the people involved.
- Keep a log of what was (or could not be) sent.
- Never slow down or fail a payment.

Not responsible for: user profiles. The only contact data it has is the email captured
when an account was opened.

## 2. Messages consumed

Queue `notifications.inbox`. Nothing is published in M2.

| Type | Recipient | Template |
|---|---|---|
| `accounts.event.account-opened.v1` | none (stores recipient) | |
| `payments.event.payment-completed.v1` | customer; for `Transfer` also the destination owner | `payment-completed`, `transfer-received` |
| `payments.event.payment-authorized.v1` | customer | `payment-authorized` |
| `payments.event.payment-captured.v1` | customer, merchant | `payment-captured`, `merchant-payment-captured` |
| `payments.event.payment-voided.v1` | customer | `payment-voided` |
| `payments.event.payment-failed.v1` | customer | `payment-failed` |
| `payments.event.payment-refunded.v1` | customer | `payment-refunded` |
| `reconciliation.event.reconciliation-mismatch-detected.v1` (Phase 2) | configured operations address | `reconciliation-mismatch` |

Recipients are resolved from account IDs in the event through `recipients`.
An event whose recipient is unknown (the `account-opened` event has not arrived yet)
is retried through the normal retry queue; after the last attempt the notification is
stored with status `Skipped` and reason `RecipientUnknown` instead of dead-lettering.

## 3. Processing

Handler, inside the consumer pipeline's transaction:

1. Resolve recipients.
2. For each `(recipient, template)`: insert a `notifications` row with status
   `Pending`. The unique key `(source_message_id, recipient_owner_id, template)` makes
   a second insert a no-op.
3. Commit. No SMTP call happens in the handler.

Sender (hosted loop, every 500 ms, `FOR UPDATE SKIP LOCKED`, batch 20):

1. Render the template (plain text, invariant culture, amounts as major units with
   the currency code).
2. Send through SMTP (`Smtp:Host`, `Smtp:Port`; Mailpit locally) with a `Message-ID`
   header derived from the notification ID.
3. Success: `Sent`, `sent_at`. Failure: `attempts += 1`,
   `next_attempt_at = now + min(2^attempts * 1 s, 5 min)`; after 6 attempts `Failed`.

## 4. HTTP API

Through the gateway under `/api/notifications/...`.

`GET /notifications?limit=&cursor=` (any authenticated user): the caller's own
notifications, newest first.

```json
{ "items": [ { "id": "...", "template": "payment-completed", "paymentId": "...",
    "status": "Sent", "subject": "Your top-up of 150.00 TRY is complete",
    "createdAt": "2026-11-13T10:15:32Z", "sentAt": "2026-11-13T10:15:32Z" } ],
  "nextCursor": null }
```

## 5. Database schema (`notifications_db`)

```sql
CREATE TABLE recipients (
    account_id uuid PRIMARY KEY,
    owner_id   uuid NOT NULL,
    email      text NOT NULL
);
CREATE INDEX ix_recipients_owner ON recipients (owner_id);

CREATE TABLE notifications (
    id                 uuid PRIMARY KEY,
    source_message_id  uuid        NOT NULL,
    recipient_owner_id uuid        NOT NULL,
    recipient_email    text        NOT NULL,
    template           text        NOT NULL,
    payment_id         uuid        NULL,
    subject            text        NOT NULL,
    body               text        NOT NULL,
    status             text        NOT NULL,     -- Pending | Sent | Failed | Skipped
    reason             text        NULL,
    attempts           int         NOT NULL DEFAULT 0,
    next_attempt_at    timestamptz NOT NULL,
    created_at         timestamptz NOT NULL,
    sent_at            timestamptz NULL,
    CONSTRAINT uq_notification UNIQUE (source_message_id, recipient_owner_id, template)
);
CREATE INDEX ix_notifications_pending ON notifications (next_attempt_at) WHERE status = 'Pending';
CREATE INDEX ix_notifications_owner ON notifications (recipient_owner_id, created_at DESC);

-- plus inbox_messages (no outbox needed in M2; added with the first published event)
```

## 6. Invariants

| # | Invariant | Enforced by |
|---|---|---|
| N1 | One event produces at most one notification row per recipient and template | Unique constraint plus inbox |
| N2 | A failing SMTP server never causes a payment event to be redelivered forever | Sending is decoupled from consuming |
| N3 | Email addresses never appear in logs or traces | Log scrubbing test |

## 7. Stated limitation

Email delivery is **at least once**. If the process dies after SMTP accepted the
message and before the row is marked `Sent`, the email is sent again. SMTP has no
idempotency key. The stable `Message-ID` lets a receiving system deduplicate, and
Mailpit shows the duplicate honestly. This is the one place in the system where a
duplicate side effect is accepted, because it moves no money.

## 8. Failure handling

| Failure | Behavior |
|---|---|
| Notifications down | Events wait in `notifications.inbox`. Payments are unaffected |
| SMTP down | Rows stay `Pending` and are retried with backoff; `Failed` after 6 attempts |
| Template rendering error | Row marked `Failed` with the reason; the sender continues with the next row |
