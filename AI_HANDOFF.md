# AI handoff — DeChimex Admin Team Chat upgrade

**Updated:** 2026-10-14  
**Status:** Implementation, regression testing, browser verification, and evidence capture are complete. No live database migration was applied.

## Task outcome

Team Chat is now a responsive, persisted, real-time staff messaging experience with bounded private attachments, image/file rendering, voice recording and playback, retry deduplication, reconnect history recovery, and consistent accessible SVG controls. Custom Order chat remains a separate customer/staff conversation scoped to its order; its association, authorization, realtime behavior, lifecycle, pricing, proof/approval, notification, and payment workflows were retained.

The pre-change comparison and root-cause analysis are documented in [`docs/TEAM_CHAT_UPGRADE.md`](docs/TEAM_CHAT_UPGRADE.md). The final browser assertions and screenshots are in [`evidence/team-chat/`](evidence/team-chat/).

## High-value implementation pointers

- `src/DeChimex.Host/Views/TeamMessages/Conversation.cshtml` and `src/DeChimex.Host/wwwroot/js/team-messages.js` — Team Chat conversation/composer, SignalR synchronization, media rendering, and recording.
- `src/DeChimex.Host/Controllers/Mvc/Admin/AdminTeamMessagesController.cs` and `src/DeChimex.Host/Controllers/Mvc/Dev/DevMessagesController.cs` — authenticated Team Chat send/history/read endpoints for the two existing staff portals.
- `src/DeChimex.Domain/Entities/InternalMessageAttachment.cs` and `src/DeChimex.Persistence/Configurations/InternalMessageConfigurations.cs` — separate internal attachment model and idempotency mapping.
- `src/DeChimex.Host/Controllers/Mvc/ChatMediaUploadSupport.cs` — shared validation/storage mechanics only; chat entities, file categories, URLs, authorization, and lifecycles remain distinct.
- `src/DeChimex.Host/Middleware/UploadAuthorizationMiddleware.cs` — private upload access checks; Team Chat and order chat use separate categories and access rules.
- `src/DeChimex.Host/Middleware/SecurityHeadersMiddleware.cs` — same-origin plus `blob:` media source needed by the recording preview; covered by `SecurityHeadersTests`.

The existing internal pairing rules remain in `src/DeChimex.Shared/Constants/InternalMessaging.cs`: Owner–Manager, Owner–Staff, Manager–Staff, Owner–Developer, Manager–Developer only. Customers, staff-to-staff, staff-to-developer, same-category, and all unlisted pairings remain denied. Never reuse order-chat membership or media authorization for Team Chat.

## Verification

- Debug solution build: **0 errors**, 13 warnings.
- Release solution build: **0 errors**, 18 warnings.
- Release UnitTests: **541/541 passed** (pre-existing full-suite run before the migration compatibility edits).
- Release IntegrationTests: **504/504 passed** (pre-existing full-suite run before the migration compatibility edits).
- Targeted migration-chain regression suite after the latest column/table/index guards: **4/4 passed**; generated SQL only, no database connection.
- EF migration check after the latest edits: **no pending model changes**.
- Final Playwright browser run: **38 assertions passed**, **0 unexpected browser/page errors**, **0 unexpected HTTP errors**. Six SignalR console logs were expected during the test's deliberately injected reconnect outage; it verified missed-message recovery and one-copy deduplication after reconnect.
- Five final screenshots are present: Team Chat desktop/mobile; Custom Order staff desktop and customer desktop/mobile. The manifest is `evidence/team-chat/browser-test-summary.json`.

The migration chain ends with `20261014132736_PersistOwnerBusinessPermissionDefaults`, then `20261014132757_TeamChatAttachments`, following `20261007182811_InternalMessages`. The Owner migration adds `OwnerBusinessPermissionDefaultsSnapshot` only when absent and retains it on rollback. After this database reported pre-existing `ClientMessageId` and `InternalMessageAttachments` objects outside EF history, the Team Chat migration was hardened to validate the key type/nullability, attachment-table columns/PK/cascade FK, and both indexes before adopting existing objects; it creates missing objects and throws on mismatches. Its `Down` deliberately retains all Team Chat schema to avoid deleting pre-existing keys or attachment metadata. **No live SQL Server migration was run by this agent**; verify the Owner column is `nvarchar(max) NULL` and the ClientMessageId column is `uniqueidentifier NULL` before retrying through the normal backed-up release process.

## Safe handoff notes

The browser run used an in-memory database, a local disabled-email outbox, fake microphone input, and temporary storage. It does not establish production SMTP, payment-provider, or live SQL behavior. The updated ZIP includes the original `appsettings.json`, `appsettings.Development.json`, and `.env.example` settings, as requested. They may contain provider, database, or seed credentials; distribute the ZIP only through an approved secure channel and restrict access. Runtime logs/data and generated uploads remain excluded. This handoff and the browser evidence manifest do not contain credential values or OTPs; redaction-test fixtures use synthetic values. For production, prefer the approved environment/secret store and rotate credentials if the archive was shared outside its intended audience.
