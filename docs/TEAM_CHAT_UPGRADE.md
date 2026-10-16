# Admin Team Chat upgrade — baseline, root causes, and verification

**Status:** Implemented and verified on 2026-10-13. This document records the pre-change comparison, the boundaries retained, the main fixes, and the evidence used for the final handoff.

## 1. Baseline assessment: Team Chat vs. Custom Order chat

These are related collaboration surfaces, but they are not the same product feature and must not share participants, lifecycle, or authorization.

| Area | Internal Team Chat (before this upgrade) | Custom Order chat (existing reference) |
|---|---|---|
| Purpose and participants | One-to-one internal work conversations. The business Admin surface is under `/admin/team-messages`; Developers use their separate `/dev/messages` surface. Customers have no access. | Conversation belonging to one `OrderId`, embedded in that order's customer/staff workspace. The order customer and authorized staff are the parties. |
| Pairing / authorization | The existing `InternalMessaging` rules are narrow: Owner–Manager, Owner–Staff, Manager–Staff, Owner–Developer, Manager–Developer. Staff–Staff and Staff–Developer are not allowed; customer, same-category, and unlisted pairs are denied. Business roles are resolved server-side, and the UI contact list mirrors those rules. | `OrderChatHub` authorizes a customer against ownership of that order, and staff against the order-workspace permission. Hub events are scoped to `order:{id}`. HTTP workspace endpoints retain their own policies. |
| Persistence and lifecycle | `InternalConversation` / `InternalMessage`, per-recipient read state, no order association. The pre-upgrade `InternalMessages` migration had message text and read/sender fields but no Team Chat attachment child table or retry id. | `OrderMessage` / `OrderMessageAttachment` are keyed to the order; order messages can include a reply reference and delivery/read state. Chat is one part of a larger lifecycle with requirements, assignment, status, quote/pricing, proof/approval, design reviews, payments, and timeline. |
| Realtime | Existing authenticated `/hubs/notifications` infrastructure could carry internal-message events. A persisted history/sync endpoint is the recovery source. | Separate authenticated `/hubs/orderchat` hub handles order-scoped message, presence, typing, delivery/read events; normal HTTP endpoints still send and load persisted state. |
| Composer/media | The existing Team Chat surface was text-oriented: its send path did not persist message attachments or a client retry key, and it lacked the Custom Order composer’s file/voice interaction. | The reference chat already had bounded upload, image/audio/video/file rendering, voice recording/playback, and responsive composer patterns. Its storage and permission rules are order-specific. |

**Non-negotiable distinction:** the upgrade borrows interaction patterns and safe upload mechanics, not the Order chat's identity, storage category, association, permissions, or business lifecycle.

## 2. Root causes established

1. **Missing Team Chat media persistence path.** The pre-upgrade `InternalMessages` schema and send path stored body/read/sender data only. There was no `InternalMessageAttachment` table or client-send idempotency key, so the UI could not reliably persist, render, authorize, or reconcile media/retries as part of a message.
2. **Composer parity gap.** Team Chat lacked the attachment staging and voice-recording controls already present in the order workspace, as well as consistent, accessible icon treatments and mobile layouts.
3. **Voice preview CSP denial.** In the first real-browser run, Chromium reported that the recording preview's `blob:` media source was blocked because the security policy had no explicit `media-src` allowance. `SecurityHeadersMiddleware` now allows same-origin media and `blob:` media; `SecurityHeadersTests` asserts that directive.
4. **Test-only public image path mismatch (not a production bug).** The first browser harness stored public product images outside the Host's static-file root, producing image 404s. Final runs put the test public root beneath `wwwroot/uploads`; the final run had no HTTP errors. Private chat media remained outside `wwwroot`.

## 3. Implementation and retained boundaries

### Team Chat

- Added `InternalMessageAttachment` as a distinct child of `InternalMessage`, with validated kind/name/storage-key/URL/size/duration metadata. Team Chat attachments use only the `TeamChatMedia` category and `teamchatmedia/` storage keys.
- Added optional `ClientMessageId` idempotency. The database migration adds a filtered unique index on `(ConversationId, SenderId, ClientMessageId)`; duplicate HTTP responses, SignalR pushes, retries, and reconnect history are deduplicated by the client.
- Kept the existing limits: **up to 6 attachments/message**, configured **10 MiB per file** default, configured extension allow-lists, and the existing multipart request ceiling. Empty-body messages are allowed only when an attachment exists; the message body remains capped at 2,000 characters.
- Reused `ChatMediaUploadSupport` for shared validation/storage mechanics only. Team Chat maps the results into its own attachment records and remains a private storage category. Uploads made before a failed persistence operation are cleaned up best-effort.
- Used authenticated SignalR delivery with automatic reconnect and history sync; there is no polling fallback. After reconnect the client fetches persisted messages from its sync endpoint and reconciles them with live events.
- Added attachment previews/downloads, voice recording, blob-backed preview and persisted audio playback, status/empty/error states, responsive layout, and accessible inline SVG controls.

### Custom Order chat

- Kept `OrderMessage`, `OrderMessageAttachment`, `ChatMedia`, the order-keyed `/hubs/orderchat` group, customer/staff access checks, and the existing order-workspace routes and lifecycle.
- The only shared component is the neutral bounded upload helper. It does not merge records, storage namespaces, or authorization. The custom-order workspace regression suite and final customer/staff browser flow both passed.

### Private media and security headers

- `LocalFileStorageService` stores both `ChatMedia` and `TeamChatMedia` in the configured private root. `UploadAuthorizationMiddleware` authorizes each media URL against its owning feature and returns indistinguishable 404s for absent/unauthorized resources; the files are not exposed by static-file serving.
- The customer browser test was denied the Team Chat page and a Team Chat attachment, while the order participant could retrieve their own order attachment.
- `media-src 'self' blob:` enables the local recorder preview without making Team Chat media public.

## 4. Database changes

The workspace's historical migration chain is included. New migrations follow the existing `20261007182811_InternalMessages` migration in this order:

1. `20261014132736_PersistOwnerBusinessPermissionDefaults` — persists the already-modelled Owner permission-default snapshot.
2. `20261014132757_TeamChatAttachments` — conditionally adds nullable `ClientMessageId`, validates and adopts an existing `InternalMessageAttachments` table only when its column/PK/cascade-FK shape matches, and creates or validates the filtered idempotency index and attachment message index. It throws rather than silently accepting schema mismatches. Its rollback intentionally retains the Team Chat columns, table, and indexes because their provenance may predate EF history.

The owner-permission migration is intentionally isolated ahead of the Team Chat schema change. After the database reported `OwnerBusinessPermissionDefaultsSnapshot` already present outside EF history, that migration was hardened: `Up` checks `COL_LENGTH` and adds the nullable column only when absent; `Down` retains it to avoid destroying data of uncertain provenance. The same database then reported both `ClientMessageId` and `InternalMessageAttachments` already present outside EF history. The Team Chat migration now adds the key only when absent, validates any existing key's `uniqueidentifier NULL` shape, adopts the attachment table only after checking its expected columns, primary key, and cascading message FK, and creates or validates both indexes. It throws on mismatches rather than silently skipping them. `Down` is intentionally non-destructive and retains all Team Chat schema because its provenance may predate EF history and the table may contain persisted attachment metadata. `dotnet ef migrations has-pending-model-changes` reported **no changes since the final migration**. **No live SQL Server migration was run by this agent.** Before rerunning `Update-Database`, verify `dbo.Roles.OwnerBusinessPermissionDefaultsSnapshot` is `nvarchar(max) NULL` and `dbo.InternalMessages.ClientMessageId` is `uniqueidentifier NULL`; the migration itself validates the attachment table and indexes. Use the normal backed-up release process.

## 5. Verification evidence

### Automated tests and builds

- Release solution build: **0 errors**, 18 warnings (existing nullable/analyzer warnings; see the build output for locations).
- Release UnitTests: **541 passed, 0 failed, 0 skipped** (the pre-existing full-suite run before the migration compatibility edits).
- Release IntegrationTests: **504 passed, 0 failed, 0 skipped** (the pre-existing full-suite run before the migration compatibility edits).
- Targeted migration-chain regression suite after the latest column/table/index guards: **4 passed, 0 failed**; generated SQL only, with no database connection.
- Debug solution build previously succeeded after the CSP fix: **0 errors**, 13 warnings.
- `dotnet ef migrations has-pending-model-changes`: no model changes pending.

### Browser run

Final Playwright run used separate authenticated Owner, Manager, Customer, and order-workspace sessions against a fresh in-memory Host, a local disabled-email outbox, a fake microphone, and isolated temporary storage. It passed **38 assertions** covering first-login setup, staff-pair rules, real-time messages in both directions, HTTP/SignalR deduplication, reconnect interruption and recovery without duplicate history, image rendering, recorded-preview and persisted-audio metadata/playback in both chat contexts, customer Team Chat denial, Team Chat media denial, and order-scoped customer/staff media.

- Unexpected browser console/page errors: **0**
- Unexpected HTTP error responses: **0**
- Expected SignalR error logs during the deliberately injected WebSocket/negotiation outage: **6**; the test released the connection and verified successful sync/recovery.
- Evidence and detailed assertion manifest: [`evidence/team-chat/browser-test-summary.json`](../evidence/team-chat/browser-test-summary.json)

Screenshots:

- `team-chat-desktop.png`
- `team-chat-mobile.png`
- `custom-order-chat-staff-desktop.png`
- `custom-order-chat-customer-desktop.png`
- `custom-order-chat-customer-mobile.png`

## 6. Handoff limitations and safety

- The browser test is a controlled in-memory/local run. It does not validate production SMTP, payment-provider calls, or a live SQL Server deployment.
- There are existing build/analyzer warnings outside this feature; no test or build errors remain.
- The evidence manifest intentionally omits account credentials, OTPs, and provider values. Redaction tests use synthetic fixtures rather than copied provider-key values. No credentials are needed to read this report. Use the project's approved secret store for any later deployment or manual login.
- Temporary browser uploads and local test Host logs/data are excluded from the deliverable. At the user's request, the ZIP includes the original `appsettings.json`, `appsettings.Development.json`, and `.env.example` settings; these may contain sensitive provider/database/seed values. Store and share the archive securely, and rotate credentials if it was exposed outside its intended audience.
