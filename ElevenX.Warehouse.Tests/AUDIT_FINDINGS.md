# ElevenX.Warehouse — รายงาน Audit (Code Review จากการเขียน Test)

> สร้างจากการทำ audit test ครบทุกฟังก์ชันใน Service layer เมื่อ 2026-06-15. ทุก finding ด้านล่าง "ถูกยืนยันด้วย test ที่ผ่าน (สีเขียว)" — คือพฤติกรรมจริงของโค้ดบน PostgreSQL จริง ไม่ใช่การเดา.

**สรุป:** 58 findings — 🔴 2 High · 🟠 15 Medium · 🟡 36 Low · 🔵 5 Info


## ⚠️ หมายเหตุสำคัญเรื่องสิทธิ์ (Authorization)

ทุกหน้า (Razor page) มี `@attribute [Authorize]` อยู่แล้ว และหน้า Users เป็น `[Authorize(Roles="ADMIN")]`. ดังนั้น finding ประเภท "read method ไม่มี permission check ใน service" เป็น **defense-in-depth ที่ขาดหายไป** (ไม่สอดคล้องกับ write method ที่เช็ค role) **ไม่ใช่ช่องโหว่ที่ผู้ใช้ทั่วไป exploit ได้ผ่าน UI ปัจจุบัน** — แต่ถ้านำ service ไปเรียกจากที่อื่น (API/endpoint ใหม่) จะไม่มีด่านป้องกัน.


---

## 🔴 HIGH

### [ExportService] Static _fontsRegistered flag is set to true even when font files are not found, permanently disabling Thai font registration process-wide
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ExportService.cs:28-36`
- **รายละเอียด:** In the constructor, when env.WebRootPath is null/wrong it falls back to the relative path "wwwroot". If the font files do not exist there, File.Exists(path) is false and RegisterFont is never called, yet _fontsRegistered is still unconditionally set to true at line 36. Because _fontsRegistered is a static once-only guard shared across the whole process, the very FIRST ExportService instance created with a bad/relative WebRootPath (e.g. when the host's content/web root differs from the deployment layout, or during early startup) permanently prevents the Kanit fonts from ever being registered. Every subsequent ToPdf call then renders Thai text with a fallback font (missing/tofu glyphs) and there is no recovery without a process restart. The flag should only be set after at least one font actually registers, or registration should not be globally short-circuited on a failed attempt. The test class works around this by force-constructing a good-path service in its static constructor before any bad-path test runs.
- **แนวทางแก้:** Only set _fontsRegistered = true after a font has actually been registered (track a local 'registered any' bool), or move font registration to app startup (Program.cs) using the resolved WebRootPath and assert the files exist. At minimum, do not flip the static guard when no font file was found.

### [LicenseService] AssignSeatAsync returns misleading 'duplicate' error when assignedToId is not a real user
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/LicenseService.cs:106-128`
- **รายละเอียด:** AssignSeatAsync never validates that assignedToId references an existing ApplicationUser. The duplicate-check (AnyAsync on ItemId+AssignedToId+active) returns false for an unknown id, so the code proceeds to SaveChangesAsync, which fails the AssignedTo FK constraint (OnDelete Restrict). The catch (DbUpdateException) block blindly returns the message 'ผู้ใช้นี้ได้รับ License/Seat ของรายการนี้อยู่แล้ว' (user already has this license). The real cause (nonexistent user) is hidden behind a wrong, confusing message. The catch is too broad: any DbUpdateException (not just IX_active_seat conflicts) is reported as a duplicate. Verified in test AssignSeat_nonexistent_user_fails_with_duplicate_message_due_to_fk.
- **แนวทางแก้:** Validate the user exists up front (e.g. db.Users.AnyAsync(u => u.Id == assignedToId)) and return a specific 'ไม่พบผู้ใช้' error; in the catch block, inspect the PostgresException SqlState (23505 unique-violation vs 23503 fk-violation) and only return the duplicate message for the IX_active_seat unique violation, otherwise rethrow or return a generic save error.


---

## 🟠 MEDIUM

### [DashboardService] UpcomingRenewals has no lower date bound, so overdue subscriptions appear as 'upcoming' with negative DaysUntil
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/DashboardService.cs:57-64`
- **รายละเอียด:** The filter is `i.NextBillingDate != null && i.NextBillingDate <= today.AddDays(30)` with no lower bound. An active recurring subscription whose NextBillingDate is in the past (e.g. a missed/overdue billing date that was never advanced) is still classified as an 'upcoming renewal'. DaysUntil = Math.Ceiling((NextBillingDate - today).TotalDays) then becomes negative (e.g. -5). The dashboard would show 'renews in -5 days', which is misleading and could mask the fact that billing automation has stalled.
- **แนวทางแก้:** Add a lower bound `i.NextBillingDate >= today` (or treat past-due dates as a separate 'overdue' bucket), and clamp/guard DaysUntil so it is never negative.

### [ExportService] ToPdf has no guard for rows whose length differs from Headers.Count, unlike ToExcel — ragged data is silently mis-aligned (or rejected) and the two exporters diverge
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ExportService.cs:123-129`
- **รายละเอียด:** ToExcel writes data with `for (var c = 0; c < colCount && c < r.Length; c++)` (ExportService.cs:80), so it safely tolerates rows shorter or longer than Headers (short rows leave blanks, long rows are truncated). ToPdf instead does `foreach (var cell in r)` with no relation to Headers.Count and a fixed column definition of exactly Headers.Count columns (line 113). A row shorter than Headers leaves the table row under-filled so cells no longer line up under their headers and every later cell shifts; a row longer than Headers overflows the column definition and QuestPDF wraps the extra cells onto a new row under the wrong headers. The same ExportTable therefore produces correct Excel but corrupt/mis-aligned PDF (or a layout exception). Any caller that builds rows whose width can vary (e.g. optional trailing columns) gets inconsistent output between the two formats.
- **แนวทางแก้:** In ToPdf, iterate by header index and clamp/pad: `for (var c = 0; c < table.Headers.Count; c++) { var text = c < r.Length ? r[c] : ""; ... }`, mirroring the ToExcel guard, so both exporters handle ragged rows identically.

### [ItemService] SKU duplicate check is case-sensitive while search is case-insensitive
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:67 (and :89 for Update)`
- **รายละเอียด:** CreateAsync/UpdateAsync detect duplicate SKUs with `i.Sku == item.Sku`, an exact, case-sensitive comparison. Search uses EF.Functions.ILike (case-insensitive). Scenario: an item exists with SKU "abc"; creating another with SKU "ABC" succeeds, yielding two items whose SKUs differ only by case. Users typically expect SKUs to be unique regardless of case, and the case-insensitive search will then surface both under one query, hinting they are 'the same'.
- **แนวทางแก้:** Compare SKUs case-insensitively for the uniqueness check, e.g. `db.Items.AnyAsync(i => i.Sku != null && EF.Functions.ILike(i.Sku, item.Sku))` (and add a case-insensitive unique index to enforce at the DB level).

### [ItemService] SKU duplicate check runs before Normalize/trim, so a padded SKU bypasses it and creates a duplicate
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:66-70`
- **รายละเอียด:** The duplicate check `AnyAsync(i => i.Sku == item.Sku)` happens on the raw, un-trimmed input, but Normalize() (which trims the SKU) runs only afterward at line 70. Scenario: an item with SKU "PAD" exists; creating an item with SKU " PAD " passes the duplicate check (" PAD " != "PAD"), then Normalize trims it to "PAD", so the database ends up with two items having SKU "PAD". UpdateAsync has the same ordering (check at :88-90, Normalize at :92). The same trim/check ordering also affects the empty-SKU path, though that one is harmless.


> **✅ verified โดย test:** พฤติกรรมจริงไม่ใช่ "สร้าง duplicate เงียบ ๆ" แต่ลอดด่านตรวจที่เป็นมิตรแล้วไปชน unique index ตอน SaveChanges → **โยน `DbUpdateException` ที่ไม่ถูก catch (HTTP 500)** แทนข้อความ "SKU ถูกใช้ไปแล้ว". รุนแรงกว่าที่ระบุไว้เดิม. pin ไว้ใน `ItemServiceTests.CreateAsync_padded_sku_bypasses_friendly_dup_check_then_throws_db_exception`.
- **แนวทางแก้:** Trim/normalize item.Sku (and Name) before performing the duplicate lookup, or perform the uniqueness check against the normalized value.

### [ItemService] CreateCategoryAsync and CreateSupplierAsync throw NullReferenceException on null name
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:153 and :169`
- **รายละเอียด:** Both methods call `name = name.Trim();` before the IsNullOrWhiteSpace validation. If a caller passes a null name, this throws NullReferenceException instead of returning the intended graceful OperationResult.Fail("กรุณาระบุชื่อ..."). The validation message is designed to handle missing input, but null never reaches it. While the Blazor UI may bind non-null strings, any other caller (or future code path) passing null gets an unhandled exception rather than a user-facing error.
- **แนวทางแก้:** Validate first: `if (string.IsNullOrWhiteSpace(name)) return Fail(...);` then `name = name.Trim();` — or use `name?.Trim() ?? string.Empty`.

### [LicenseService] All read methods lack any permission/authorization check
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/LicenseService.cs:22-88`
- **รายละเอียด:** GetAssignmentsAsync, GetUsedSeatsMapAsync, GetUsedSeatsAsync, GetSeatItemsAsync, and GetSeatUsageAsync perform no CanManageAsync (or any role) check. An anonymous caller or any role can read every license assignment including the assignee's FullName and Email. Only the write methods (Assign/Release) are gated. If these service methods are reachable from a page that is not itself authorized, this leaks user PII. Verified in test GetAssignments_has_no_permission_check_returns_data_for_anonymous.
- **แนวทางแก้:** If license/seat data is meant to be admin/purchaser-only, add a role check (or at least an authenticated check) to the read methods, or ensure every Razor page/component that calls them enforces authorization via @attribute [Authorize(Roles=...)].

### [PurchaseService] GetPurchasesAsync performs no authorization check
- **ตำแหน่ง:** `elevenx-warehouse/Services/PurchaseService.cs:25-49`
- **รายละเอียด:** Unlike RecordPurchaseAsync and DeleteAsync, GetPurchasesAsync never calls CanManageAsync(). A PurchaseService built with an anonymous/no-role accessor (or any logged-in user with no role) can read the full purchase/expense history including supplier, unit prices, totals, notes and purchaser identity. The test GetPurchases_is_readable_without_management_permission confirms an anonymous accessor returns all rows. If purchase history is meant to be restricted (it contains spend data), this is a permission gap. Likely fine if any authenticated user is allowed to view, but there is no server-side gate at all.
- **แนวทางแก้:** If reads should be restricted, add an at-least-Viewer (or CanManage) role check at the top of GetPurchasesAsync; otherwise document explicitly that purchase history is readable by anyone and ensure the UI route is authorized.

### [PurchaseService] Stock/seat reversal on delete silently floors at 0, losing the true delta
- **ตำแหน่ง:** `elevenx-warehouse/Services/PurchaseService.cs:106-109`
- **รายละเอียด:** DeleteAsync reverses a purchase with Math.Max(0, current - purchase.Quantity). If stock/seats were already consumed below the purchased quantity (e.g. item now has 5 units but the purchase being deleted added 30), the result is clamped to 0 instead of going negative or being rejected. Tests Delete_iot_floors_stock_at_zero_when_already_reduced_below_quantity and Delete_seat_reversal_floors_at_zero show this. While clamping avoids negative stock, it produces an incorrect post-delete count: the 25 units that had been withdrawn/assigned are effectively erased, so the on-hand figure no longer reflects reality. Deleting an old replenishment purchase can therefore zero out legitimately remaining stock. Consider blocking deletion when reversal would drive the count negative, or recomputing on-hand from the full ledger rather than mutating a denormalized counter.
- **แนวทางแก้:** Either reject the delete when purchase.Item.Quantity (or TotalSeats) < purchase.Quantity with a clear error, or stop storing a mutable counter and derive on-hand stock/seats from the purchase/withdrawal/assignment ledger so deletes recompute consistently.

### [ReportService] License usage report can show Used > Total (over-allocation not flagged)
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ReportService.cs:111`
- **รายละเอียด:** GetLicenseUsageReportAsync returns Used = count of active LicenseAssignments and Total = TotalSeats ?? 0, with no comparison between them. When TotalSeats is null (Total=0) or fewer than the number of active assignments, the report returns e.g. Used=3 / Total=1 or Used=1 / Total=0 with no warning/flag. The report surfaces an inconsistent (over-allocated) state silently; a consumer rendering 'Used/Total' would show 3/1 or 1/0 with no indication anything is wrong. Reproduced in tests GetLicenseUsage_total_seats_null_reported_as_zero and GetLicenseUsage_used_can_exceed_total_when_more_active_than_seats.
- **แนวทางแก้:** Expose an Available/over-allocated indicator (e.g. mirror SeatUsage.Available = Math.Max(0, Total - Used) and an IsOverAllocated flag) so the report distinguishes a genuinely full seat from an inconsistent over-allocation, especially when TotalSeats is null.

### [SubscriptionService] GetUpcomingRenewalsAsync has no lower date bound, surfacing overdue subscriptions with negative DaysUntil
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:42-53`
- **รายละเอียด:** The filter only checks NextBillingDate <= today.AddDays(withinDays) with no lower bound. An Active recurring subscription whose NextBillingDate is already in the past (e.g. a missed/never-recorded charge) is returned as an 'upcoming renewal' with a NEGATIVE DaysUntil (e.g. -3 for a date 3 days ago). UI that renders 'renews in N days' or sorts/labels by DaysUntil will show nonsensical negative values, and overdue items get mixed in with genuinely upcoming ones. Verified by GetUpcomingRenewals_past_due_active_is_included_with_negative_daysUntil.
- **แนวทางแก้:** Add a lower bound (e.g. NextBillingDate >= today) or treat overdue items as a separate 'overdue' bucket; clamp DaysUntil to >= 0 if negative values are not intended.

### [SubscriptionService] ReactivateAsync can revive an expired subscription whose EndDate has already passed
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:115-130`
- **รายละเอียด:** Reactivate unconditionally sets Status=Active and pushes NextBillingDate forward via Advance(today,cycle) without ever consulting EndDate. A subscription that was correctly flipped to Expired because its contract EndDate passed (see RecordRecurringChargeAsync line 93-94) can be silently turned back into a billable Active subscription with a fresh future NextBillingDate that is past EndDate. The very next RecordRecurringChargeAsync would then immediately mark it Expired again. Verified by Reactivate_expired_past_endDate_does_not_re_expire_or_validate_endDate.
- **แนวทางแก้:** In ReactivateAsync, if EndDate is set and EndDate < today (or the computed NextBillingDate > EndDate), reject reactivation or keep Status=Expired with an informative error.

### [UserService] GetUsersAsync has no permission guard - any caller (even anonymous) can enumerate all users and emails
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/UserService.cs:27`
- **รายละเอียด:** Every other method in UserService starts with `if (!await IsAdminAsync()) return Fail(Forbidden)`, but GetUsersAsync has no such check. A caller with no role at all (Db.Anonymous()) successfully retrieves the full list of users including their Id, Email, FullName, Role, and lockout status. This is an information-disclosure / permission gap inconsistent with the rest of the service. Verified by test GetUsersAsync_has_no_permission_guard_anonymous_can_read_all_users.
- **แนวทางแก้:** Add `if (!await IsAdminAsync()) return new List<UserRow>();` (or throw/return an empty list) at the top of GetUsersAsync, or gate the admin page that calls it. If the page is already [Authorize(Roles=ADMIN)] gated this is lower risk, but the service-level inconsistency remains.

### [UserService] CreateUserAsync can leave an orphaned user (created but with no role) if AddToRoleAsync fails
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/UserService.cs:57`
- **รายละเอียด:** After `userManager.CreateAsync(user, password)` succeeds, the code calls `AddToRoleAsync`. If that second step fails, the method returns Fail(...) but never deletes the already-persisted user. The result: a user exists in the DB with a confirmed email and a valid password but NO role, and the admin sees a failure message. A retry with the same email then fails with 'duplicate email'. The role assignment is not wrapped in a transaction / compensating delete. (Role assignment is unlikely to fail here because EnsureRoleExists runs first, but the non-atomic create-then-assign pattern is a latent bug.)
- **แนวทางแก้:** Wrap the create+role-assign in a transaction, or on AddToRoleAsync failure call `await userManager.DeleteAsync(user)` before returning the error so no orphaned account remains.

### [WithdrawalService] GetWithdrawalsAsync date filter is asymmetric: `from` uses full timestamp but `to` is day-normalized
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:36-37`
- **รายละเอียด:** `to` is normalized to a whole day via `w.WithdrawnAt < to.Value.Date.AddDays(1)` (inclusive of the entire `to` day, time component ignored), but `from` is applied as `w.WithdrawnAt >= from` using the raw timestamp with NO .Date normalization. So a UI that passes a from-date carrying a non-midnight time (e.g. DateTime.Today after some local conversion, or a value with hours/minutes) will silently drop withdrawals that occurred earlier on that same calendar day. A date-range picker selecting from=10 May, to=10 May would unexpectedly exclude part of the 10th if `from` is not exactly midnight. The two boundaries should be treated consistently (both normalized to .Date).
- **แนวทางแก้:** Normalize `from` the same way: `q = q.Where(w => w.WithdrawnAt >= from.Value.Date);` so both bounds are day-inclusive and symmetric.

### [WithdrawalService] RecordWithdrawalAsync does not validate withdrawnById; empty/invalid ids are persisted
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:49-80`
- **รายละเอียด:** `withdrawnById` is taken as-is and written straight into the Withdrawal row with no null/empty check and no verification that the user exists. Passing "" (or any non-existent id) succeeds and creates a withdrawal with no resolvable owner. Because Withdrawal.WithdrawnById defaults to "" and the FK is not enforced against a real user here, this breaks auditability (cannot tell who withdrew stock) and would make GetWithdrawalsAsync's Include(WithdrawnBy) navigation null for that row. Normally the id comes from CurrentUserAccessor, but the service trusts the caller-supplied value instead of resolving it internally.


> **✅ verified โดย test:** FK `FK_Withdrawals_AspNetUsers_WithdrawnById` ถูกบังคับจริง — id ว่าง/ไม่มีตัวตนไม่ได้ถูก persist แต่ **โยน `DbUpdateException` ที่ไม่ถูก catch (HTTP 500)** และ transaction rollback (สต็อกไม่ถูกหัก). pin ไว้ใน `WithdrawalServiceTests.RecordWithdrawal_empty_withdrawnById_throws_unhandled_db_exception`.
- **แนวทางแก้:** Either resolve the withdrawer from currentUser.GetUserIdAsync() inside the service, or validate that withdrawnById is non-empty and corresponds to an existing ApplicationUser before saving; return a Fail otherwise.


---

## 🟡 LOW

### [CurrentUserAccessor] IsInAnyRoleAsync compares role names case-sensitively
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/CurrentUserAccessor.cs:37`
- **รายละเอียด:** IsInAnyRoleAsync uses ClaimsPrincipal.IsInRole, which performs an ordinal (case-sensitive) comparison against the role claims. Roles are stored uppercase (AppRoles.Admin = "ADMIN"). If any caller passes a differently-cased string (e.g. "admin" or "Admin"), the check silently returns false and the user is denied permission even though they actually hold the role. Verified by test: IsInAnyRoleAsync("admin") returns false while IsInAnyRoleAsync("ADMIN") returns true for the same Admin principal. Today all callers should use the AppRoles constants, but it is a latent footgun.
- **แนวทางแก้:** Compare case-insensitively, e.g. evaluate roles against the principal's role claims using StringComparer.OrdinalIgnoreCase, or normalize role names before comparison.

### [CurrentUserAccessor] IsInAnyRoleAsync() with no arguments silently returns false
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/CurrentUserAccessor.cs:34-38`
- **รายละเอียด:** Because the implementation is roles.Any(...), calling IsInAnyRoleAsync() with an empty params array returns false unconditionally (Any over an empty sequence is false), regardless of who is logged in. A caller that accidentally passes an empty/null-coalesced-to-empty role list will get a hard 'denied' with no error, which can mask a wiring bug rather than surface it.
- **แนวทางแก้:** Consider guarding against an empty roles argument (throw ArgumentException or treat as a programming error), so an empty role check is not mistaken for a legitimate permission denial.

### [CurrentUserAccessor] GetDisplayNameAsync issues a full DB round-trip per call (no caching)
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/CurrentUserAccessor.cs:27-31`
- **รายละเอียด:** GetDisplayNameAsync delegates to GetUserAsync, which calls UserManager.GetUserAsync -> a DB query every invocation. The display name (and user id) are already derivable from claims for the common case, so rendering the display name in UI hot paths performs an unnecessary database hit each time. Not a correctness bug, but a per-render efficiency concern if this is invoked frequently in components.
- **แนวทางแก้:** Where only the display name is needed, read it from claims (Name claim) when available, or memoize the resolved user per request/scope to avoid repeated DB lookups.

### [DashboardService] SeatsNearFull flags brand-new single-seat items that are completely empty (Available<=1 with Used=0)
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/DashboardService.cs:86-89`
- **รายละเอียด:** Near-full is `s.Total > 0 && (s.Available <= 1 || s.UsedRatio >= 0.9)`. For an item with TotalSeats=1 and Used=0, Available=1 satisfies `Available <= 1`, so it is reported as 'near full' even though UsedRatio is 0.0 and no seat has been used. Single-seat products would always show as near-full from creation, generating false warnings on the dashboard.
- **แนวทางแก้:** Combine the absolute-headroom check with usage, e.g. `s.Used > 0 && (s.Available <= 1 || s.UsedRatio >= 0.9)`, or only apply the `Available <= 1` rule when Total is large enough for that to be meaningful (e.g. Total >= 2).

### [DashboardService] RecentActivity limits each source to Take(8) before merging, so a busy single source is truncated below the 12-row display cap
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/DashboardService.cs:113-138`
- **รายละเอียด:** Purchases, withdrawals and licenses are each independently capped with Take(8) BEFORE the merge, then the merged list is ordered by When desc and Take(12). If only one source has activity (e.g. 10 recent purchases and no withdrawals/licenses), the user sees at most 8 entries even though the display can hold 12 and there are 10 genuinely-recent records. The 9th and 10th newest purchases are silently dropped. Conversely the merge can also surface an older record from a quiet source ahead of a newer record that fell outside another source's Take(8).
- **แนวทางแก้:** Fetch more rows per source (e.g. Take(12)) or fetch each source ordered and then re-sort and Take(12), so the final 12 are truly the 12 most recent across all sources.

### [DashboardService] IotStockValue and LowStock do not guard against negative current Quantity
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/DashboardService.cs:34-40`
- **รายละเอียด:** iotStockValue accumulates `i.Quantity * avg` using the raw current Item.Quantity. If stock has gone negative (over-withdrawal), the contribution is negative and can drag the total stock value below zero, which is nonsensical for a valuation. (Related: LowStock correctly flags negative stock, but the valuation should arguably clamp at 0.)
- **แนวทางแก้:** Clamp the per-item contribution with Math.Max(0, i.Quantity) when computing valuation, or surface negative stock as a separate data-integrity warning.

### [ExportService] ToExcel assigns raw cell values without the null-coalescing guard that ToPdf uses
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ExportService.cs:81`
- **รายละเอียด:** ToPdf renders cells with `Text(cell ?? "")` (line 127), defensively handling a null entry in a row array. ToExcel does `ws.Cell(row, c + 1).Value = r[c]` (line 81) with no null guard. ClosedXML currently tolerates a null string (renders a blank cell), so this does not throw today, but the asymmetry is fragile: if cell value handling changes or a non-string overload path is hit, the two exporters could behave differently on the same null-containing data. Test ToExcel_with_null_cell_value_in_row_does_not_throw confirms current tolerance.
- **แนวทางแก้:** Use the same `r[c] ?? ""` (or `string.Empty`) coalescing in ToExcel for symmetry with ToPdf.

### [ExportService] Sanitize truncates worksheet name by UTF-16 code-unit index, which can split a surrogate pair or trailing combining mark
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ExportService.cs:149`
- **รายละเอียด:** Sanitize returns `name.Length > 31 ? name[..31] : name`. The slice is by .NET char (UTF-16 code unit) count, not grapheme/rune count. Thai text is in the BMP so each char is one code unit (fine), but a title containing an astral-plane character (emoji, rare CJK ext) at the boundary would be cut mid-surrogate, producing an invalid/garbled worksheet name. Excel's 31-char limit is also itself char-count based so this rarely throws, but the truncation is not surrogate-safe. Only the worksheet name is affected; the title cell uses the full unsanitized string.
- **แนวทางแก้:** Truncate using StringInfo / EnumerateRunes to avoid splitting surrogate pairs, e.g. take the first 31 runes, or guard the slice with char.IsHighSurrogate before cutting.

### [ItemService] Category name duplicate check is case-sensitive
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:156`
- **รายละเอียด:** `db.Categories.AnyAsync(c => c.Name == name)` is an exact comparison. Scenario: category "camera" exists; CreateCategoryAsync("Camera") succeeds, creating a near-duplicate category. The GetCategoriesAsync dropdown will then show two visually-similar entries, splitting items across them. This is inconsistent with the apparent intent of preventing duplicate categories.
- **แนวทางแก้:** Use a case-insensitive comparison, e.g. EF.Functions.ILike(c.Name, name), and/or a case-insensitive unique index on Category.Name.

### [ItemService] Recurring subscription with Cancelled/Expired status and null NextBillingDate is persisted with NextBillingDate = null
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:210-211`
- **รายละเอียด:** Normalize only backfills a default NextBillingDate when Status == Active. A recurring item saved with Status Cancelled or Expired and a null NextBillingDate keeps NextBillingDate null. If a Cancelled/Expired subscription is later reactivated by an UpdateAsync that sets Status=Active, that path will then default it — but any reporting/dashboard logic that reads NextBillingDate for non-Active recurring items must tolerate null. This is likely intentional but is an easy-to-miss null for downstream consumers.
- **แนวทางแก้:** Confirm all consumers of NextBillingDate null-guard for non-Active recurring items; consider documenting that NextBillingDate is only guaranteed non-null for Active subscriptions.

### [ItemService] Supplier contact normalizes whitespace to empty string rather than null
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:173`
- **รายละเอียด:** `Contact = contact?.Trim()` converts a whitespace-only contact ("   ") into "" (empty string), while a truly absent contact stays null. This produces two distinct 'no contact' representations (null vs "") in the Supplier table, which complicates display logic and equality/filter checks downstream.
- **แนวทางแก้:** Normalize blank contact to null: `Contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim();` (mirroring how Sku is normalized in Normalize()).

### [LicenseService] GetSeatItemsAsync includes Server/Software items with null TotalSeats while GetSeatUsageAsync excludes them
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/LicenseService.cs:61-88`
- **รายละเอียด:** GetSeatItemsAsync returns all Server/Software items regardless of TotalSeats, so such an item appears in an assignment dropdown; but AssignSeatAsync immediately rejects it with 'รายการนี้ยังไม่ได้กำหนดจำนวน License/Seat'. Meanwhile GetSeatUsageAsync filters to TotalSeats != null. The two methods are inconsistent about what counts as a seat-bearing item, leading to a selectable-but-unusable option in the UI. Verified in GetSeatItems_includes_server_software_even_without_total_seats.
- **แนวทางแก้:** Either filter GetSeatItemsAsync to i.TotalSeats != null (and > 0) to match assignment eligibility, or surface a clear disabled/warning state in the UI for items without configured seats.

### [LicenseService] Search input is not escaped before being used as an ILike pattern (wildcard injection)
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/LicenseService.cs:34-38`
- **รายละเอียด:** The search term is interpolated directly into the pattern as %{search.Trim()}% and passed to EF.Functions.ILike. The %, _ and \ characters keep their LIKE-wildcard meaning, so a user searching for '%' matches every row, and '_' matches any single char. Not a SQL-injection risk (parameterized) but produces surprising/incorrect search results. Verified in GetAssignments_search_with_percent_is_treated_literally_by_ilike_wildcard.
- **แนวทางแก้:** Escape LIKE metacharacters in the user input before building the pattern (replace \\, %, _ with their escaped forms and use ILike(col, pattern, "\\")), or use a full-text/normalized comparison.

### [LicenseService] GetSeatUsageAsync ordering by UsedRatio has nondeterministic tie-breaking
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/LicenseService.cs:84-87`
- **รายละเอียด:** Results are ordered solely by UsedRatio descending. The ordering happens in-memory (LINQ-to-objects after projection) and there is no secondary sort key, so items with equal ratios (e.g. several 0-used items at ratio 0) come back in an arbitrary order that can differ between calls. This makes any UI that relies on stable ordering flaky and complicates testing. Tests avoid asserting tie order for this reason.
- **แนวทางแก้:** Add a deterministic secondary sort, e.g. .OrderByDescending(s => s.UsedRatio).ThenBy(s => s.Name) (or ThenBy(s => s.ItemId)).

### [PurchaseService] RecordPurchaseAsync does not validate the purchasedById / supplierId references or the date
- **ตำแหน่ง:** `elevenx-warehouse/Services/PurchaseService.cs:51-93`
- **รายละเอียด:** purchasedById is taken verbatim and assigned to a non-nullable FK; an invalid id would only fail at SaveChanges with a raw DB FK exception rather than a friendly OperationResult (callers always pass the current user id, so this is latent). Likewise supplierId is not checked for existence. The date parameter is accepted with no bounds, so a purchase can be dated arbitrarily far in the future, which would skew date-range spend reports. Tests RecordPurchase_accepts_future_date_without_validation and the supplier/note persistence test document current behavior.
- **แนวทางแก้:** Validate that supplierId (when provided) exists and return OperationResult.Fail with a friendly message; optionally reject dates in the far future, and wrap SaveChanges to translate FK violations into user-facing errors.

### [PurchaseService] TotalCost stored at decimal(12,2) can diverge from the computed value due to rounding
- **ตำแหน่ง:** `elevenx-warehouse/Services/PurchaseService.cs:74`
- **รายละเอียด:** TotalCost = quantity * unitPrice is computed at full decimal precision, but both UnitPrice and TotalCost columns are decimal(12,2), so values are rounded on persist. Example covered by RecordPurchase_persisted_total_cost_rounds_to_two_decimals: quantity 3 * unitPrice 0.333 yields an in-memory TotalCost of 0.999 but the DB stores 1.00, and UnitPrice 0.333 stores as 0.33. The returned OperationResult.Value carries the un-rounded in-memory object, so the value the caller sees (0.999) differs from what is persisted and later read back (1.00). Aggregated reports summing the stored column will not match summing computed values. Minor, but money rounding/representation is inconsistent between the in-memory result and the database.
- **แนวทางแก้:** Round UnitPrice and TotalCost to 2 decimals explicitly (decimal.Round) before assigning, and/or reload/return the persisted entity so the caller's value matches the stored value; reject unitPrice values with more than 2 decimal places.

### [PurchaseService] Type=Other purchases and unitPrice=0 are accepted with no stock/seat effect and no cost guard
- **ตำแหน่ง:** `elevenx-warehouse/Services/PurchaseService.cs:58-88`
- **รายละเอียด:** A purchase against an ItemType.Other item records a Quantity and TotalCost but updates neither Quantity nor TotalSeats (only IoT and Server/Software branches mutate the item), so the recorded quantity is effectively meaningless for Other items (test RecordPurchase_other_type_touches_neither_quantity_nor_seats). Separately, unitPrice == 0 is allowed, producing TotalCost 0 (test RecordPurchase_zero_unit_price_is_accepted); this may be intentional for free/comp items but there is no flag distinguishing an intended zero from a forgotten price entry.
- **แนวทางแก้:** Consider validating that Other-type items either disallow stock-style quantities >1 or document the no-op; if a zero price should be exceptional, require an explicit confirmation/flag rather than silently accepting it.

### [PureHelpers] MonthlyEquivalent never rounds; indivisible amounts produce long repeating decimals
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/BillingMath.cs:18`
- **รายละเอียด:** MonthlyEquivalent(amount, Quarterly) returns amount/3m and Yearly returns amount/12m with no rounding. For an indivisible amount such as 100m Quarterly the result is 33.333... carried to decimal's full 28-29 significant digits. Callers that sum many per-month equivalents (e.g. the monthly subscription total on the dashboard) can accumulate fractional drift versus the true annual figure, and the unrounded long-tail value may render badly if displayed directly. Behavior is asserted in the tests as-is.
- **แนวทางแก้:** If a monetary total is intended, round at the boundary, e.g. Math.Round(amount / 3m, 2, MidpointRounding.AwayFromZero) (or have the caller round the aggregate), and document the rounding policy.

### [PureHelpers] Item.IsLowStock flags items with MinQuantity=0 and Quantity=0 as low stock
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Data/Item.cs:57`
- **รายละเอียด:** IsLowStock is `Type == IotMaterial && Quantity <= MinQuantity`. Because the comparison is inclusive and the default MinQuantity is 0, any IoT item that is intentionally not stocked (Quantity 0, MinQuantity 0) is reported as low stock. This can flood the low-stock dashboard/alert list with items that have no reorder threshold configured.
- **แนวทางแก้:** Consider requiring a positive threshold, e.g. `MinQuantity > 0 && Quantity <= MinQuantity`, so items with no configured reorder point are not alerted, or surface a separate 'out of stock' vs 'below threshold' distinction.

### [PureHelpers] Item.TracksSeats uses HasValue, not >0, so TotalSeats=0 still 'tracks seats'
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Data/Item.cs:58`
- **รายละเอียด:** TracksSeats is `Type is Server or Software && TotalSeats.HasValue`. A Server/Software item with TotalSeats explicitly set to 0 returns true even though there are no seats to assign. Any seat-assignment UI gated on TracksSeats would be enabled for an item that cannot legitimately hold a license, and SeatUsage.UsedRatio would then hit its Total<=0 zero-guard while Available is also 0.
- **แนวทางแก้:** If zero seats should be treated as 'not seat-tracked', gate on `TotalSeats > 0` instead of `.HasValue`.

### [PureHelpers] SeatUsage.UsedRatio is uncapped and can exceed 1.0 when oversubscribed
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/Dtos.cs:36`
- **รายละเอียด:** UsedRatio returns (double)Used/Total with no upper clamp, so an oversubscribed item (Used 15, Total 10) yields 1.5. A progress-bar UI bound directly to UsedRatio would render past 100% unless it clamps independently. Note Available is correctly clamped to 0 in the same record, so the two computed members disagree on how they treat oversubscription.
- **แนวทางแก้:** Clamp the ratio, e.g. `Math.Min(1.0, (double)Used / Total)`, or ensure the UI clamps, to keep Available and UsedRatio consistent for oversubscribed seats.

### [PureHelpers] LicenseAssignment.IsActive is a pure null check; a future ReleasedAt deactivates immediately
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Data/LicenseAssignment.cs:23`
- **รายละเอียด:** IsActive is `ReleasedAt is null`. If a release is scheduled for a future date by setting ReleasedAt ahead of time, the seat is counted as inactive (released) the moment the value is set rather than on the release date. Used-seat counts derived from IsActive would understate current usage. Likewise default(DateTime) (0001-01-01) is non-null and so reads as released.
- **แนวทางแก้:** If future-dated releases are a use case, compare against now: `ReleasedAt is null || ReleasedAt > DateTime.UtcNow`; otherwise document that ReleasedAt must only be set at the moment of release.

### [PureHelpers] OperationResult<T>.Ok accepts null and reports Success=true
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/OperationResult.cs:12`
- **รายละเอียด:** OperationResult<T>.Ok(value) performs no null check, so OperationResult<string>.Ok(null) returns Success=true with Value=null. A caller that branches on Success and then dereferences Value can still hit a NullReferenceException despite the 'success' contract.
- **แนวทางแก้:** Either annotate Ok with a non-null parameter and guard (ArgumentNullException) for reference types, or document that Success guarantees no error message but not a non-null Value.

### [ReportService] Spend/withdrawal reports use date-granularity, silently stripping caller-supplied time-of-day on 'from'
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ReportService.cs:24`
- **รายละเอียด:** Both GetSpendReportAsync (line 24/30) and GetWithdrawalReportAsync (line 70/73) compute the window as p.Date >= from.Date && p.Date < to.Date.AddDays(1). The .Date call drops any time component the caller passes in 'from' and 'to'. A caller passing from=2026-03-10T15:00 expecting an intraday window will still get the 08:00 purchase on 2026-03-10 (verified in GetSpendReport_from_time_component_is_ignored_uses_date_only). This is fine if reports are documented as date-granular, but the public DateTime signature implies timestamp precision that the implementation does not honor.
- **แนวทางแก้:** Either accept DateOnly in the signature to make date-granularity explicit, or honor the supplied time component (use raw from/to instead of .Date) if intraday filtering is intended.

### [ReportService] Inverted date range (to < from) silently returns an empty report with no error
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ReportService.cs:24`
- **รายละเอียด:** When the caller passes to earlier than from, toExclusive = to.Date.AddDays(1) is still <= from.Date, so the Where clause matches nothing and an empty SpendReportResult / empty withdrawal list is returned. There is no validation or error, so a caller (or UI date-picker bug) that swaps the dates gets a misleading 'no data' result indistinguishable from a legitimately empty period. Verified in GetSpendReport_inverted_range_to_before_from_returns_nothing.
- **แนวทางแก้:** Validate from <= to and either swap, clamp, or return an OperationResult-style failure so callers can distinguish a bad range from an empty period.

### [ReportService] Spend report nets negative TotalCost into 'spend' totals with no guard
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ReportService.cs:53`
- **รายละเอียด:** GrandTotal/OneTimeTotal/RecurringTotal and each row sum p.TotalCost directly. A negative TotalCost (e.g. a refund/credit recorded as a Purchase) nets down the spend totals, so a 100 purchase plus a -30 entry reports GrandTotal=70 while PurchaseCount=2 (verified in GetSpendReport_negative_total_cost_is_summed_as_is). Whether refunds should reduce 'spend' is a product decision, but the report makes no distinction and there is no validation preventing negative costs at write time.
- **แนวทางแก้:** Decide on refund handling explicitly: either disallow negative TotalCost upstream, or separate refunds/credits from gross spend in the report so the meaning of GrandTotal is unambiguous.

### [SubscriptionService] RecordRecurringChargeAsync still creates a Purchase and advances NextBillingDate past EndDate even when it flips the sub to Expired
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:89-94`
- **รายละเอียด:** When the new periodEnd exceeds EndDate, the code sets Status=Expired but has ALREADY added the Purchase for that beyond-the-contract period and sets NextBillingDate = periodEnd (a date past EndDate). So the system records (and bills for) a recurring charge covering a period that runs past the subscription's contractual end, and leaves a stale future NextBillingDate on an Expired item. Whether this final over-the-end charge should be created is questionable. Verified by RecordRecurringCharge_sets_status_expired_when_periodEnd_exceeds_endDate.
- **แนวทางแก้:** Decide the intended semantics: either reject the charge when periodStart >= EndDate (no purchase, just expire), or clamp periodEnd/NextBillingDate to EndDate; do not advance NextBillingDate beyond EndDate on an Expired item.

### [SubscriptionService] CancelAsync leaves a populated future NextBillingDate on a cancelled subscription
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:101-113`
- **รายละเอียด:** Cancel only sets Status=Cancelled; it does not clear NextBillingDate. A cancelled subscription therefore still carries a future billing date. GetUpcomingRenewalsAsync filters on Status==Active so it is safe there, but any other consumer that reads NextBillingDate without also checking Status (reports, reminders, exports) could treat a cancelled sub as still due. Verified by Cancel_does_not_change_nextBillingDate.
- **แนวทางแก้:** Consider setting NextBillingDate = null on cancel, or ensure every consumer of NextBillingDate also filters on Status == Active.

### [SubscriptionService] RecordRecurringChargeAsync default note uses ?? so an empty-string note is persisted verbatim instead of the cycle label
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:87`
- **รายละเอียด:** Note = note ?? cycle-label default only substitutes when note is null. If a caller passes an empty/whitespace string (a common outcome from an unbound text input), the Purchase is saved with a blank Note instead of the meaningful default cycle label, degrading the audit trail. Verified by RecordRecurringCharge_empty_string_note_is_not_replaced_by_default.
- **แนวทางแก้:** Use string.IsNullOrWhiteSpace(note) ? default : note so blank input falls back to the cycle label.

### [UserService] GetUsersAsync IsLockedOut ignores LockoutEnabled - only checks LockoutEnd
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/UserService.cs:34`
- **รายละเอียด:** IsLockedOut is computed as `u.LockoutEnd is not null && u.LockoutEnd > DateTimeOffset.UtcNow`, without considering `LockoutEnabled`. Identity treats a user as actually locked out only when LockoutEnabled is also true (UserManager.IsLockedOutAsync checks both). In this app SetActiveAsync always sets LockoutEnabled=true when disabling, so the two stay in sync today; however if LockoutEnd were ever set while LockoutEnabled is false (e.g. failed-login lockout policy, or external mutation), the UI would report the user as locked/disabled when Identity would still allow them to sign in. The displayed status can diverge from the real sign-in behavior.
- **แนวทางแก้:** Either reuse `await userManager.IsLockedOutAsync(u)` for the row flag, or include `u.LockoutEnabled` in the condition: `u.LockoutEnabled && u.LockoutEnd is not null && u.LockoutEnd > DateTimeOffset.UtcNow`.

### [UserService] CreateUserAsync/UpdateUserAsync call fullName.Trim() without a null guard -> NullReferenceException
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/UserService.cs:54`
- **รายละเอียด:** Both CreateUserAsync (line 54: `FullName = fullName.Trim()`) and UpdateUserAsync (line 83: `user.FullName = fullName.Trim()`) call .Trim() directly on the fullName argument. If a caller passes null fullName, this throws NullReferenceException rather than returning a clean OperationResult.Fail. Email is also dereferenced via email.Trim() at line 43 before the admin check is even relevant; a null email throws NRE too. Inputs from the Blazor form are unlikely to be null, but the service contract does not defend against it (no [NotNull] / validation).
- **แนวทางแก้:** Coalesce or validate inputs: e.g. `fullName = (fullName ?? string.Empty).Trim();` and reject null/blank email/fullName with a friendly OperationResult.Fail before touching Identity.

### [UserService] UpdateUserAsync role-swap condition is redundant/confusing and always re-swaps when role count != 1
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/UserService.cs:87`
- **รายละเอียด:** The guard `if (!currentRoles.Contains(role) || currentRoles.Count != 1)` controls whether to remove-all-then-add. The only case it skips the swap is when the user has exactly one role and it equals the target. That is correct, but the `|| currentRoles.Count != 1` branch means a user who already has the target role plus extra roles will have ALL roles (including the target) removed and the single target re-added - acceptable but the intent is unclear and the double condition is easy to misread. More notably, if RemoveFromRolesAsync or the subsequent AddToRoleAsync fails, those failures are silently ignored (return value not checked), unlike CreateUserAsync which checks AddToRoleAsync. A failed role change could return Success while the role did not actually change.
- **แนวทางแก้:** Simplify to a single intent (e.g. if the user's current single role already equals target, do nothing; else replace) and check the IdentityResult of RemoveFromRolesAsync/AddToRoleAsync, returning Fail(JoinErrors(...)) on failure as CreateUserAsync does.

### [WithdrawalService] DeleteAsync returns stock unconditionally with no double-count protection
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:100-103`
- **รายละเอียด:** DeleteAsync adds the withdrawal quantity back to Item.Quantity whenever the item is IoT, with no cap and no record that this withdrawal's deduction is still 'in effect'. If stock was independently adjusted/restocked, or if the item's deduction was already reversed by some other path, deleting the withdrawal over-credits stock (quantity can exceed any prior real value). There is no upper bound or reconciliation. While individually each delete is a clean inverse of its own record, the operation is not idempotent-safe against concurrent stock edits and can inflate inventory.
- **แนวทางแก้:** Consider performing the stock return inside an explicit transaction with a re-read of current quantity, and/or tracking a 'reversed' flag, or at minimum document that delete blindly re-credits. Optionally guard against negative-impossible states.

### [WithdrawalService] No validation that `when` (WithdrawnAt) is not in the future
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:65-71`
- **รายละเอียด:** RecordWithdrawalAsync accepts any `when` value, including dates far in the future. A future-dated withdrawal is persisted and will appear at the top of GetWithdrawalsAsync (ordered by WithdrawnAt desc) and skew any time-window reporting. There is no clamp to UtcNow nor a sanity bound.
- **แนวทางแก้:** Reject or clamp `when` greater than DateTime.UtcNow (allowing a small skew tolerance) to keep timeline/reporting consistent.

### [WithdrawalService] GetWithdrawableItemsAsync returns IoT items with zero stock despite contract saying 'still have stock to withdraw'
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:83-90`
- **รายละเอียด:** The interface doc comment states 'รายการ IoT ที่ยังมีสต็อกให้เบิก' (IoT items that still have stock to withdraw), but the query filters only on Type == IotMaterial with no Quantity > 0 condition. Items with Quantity == 0 are returned and would be offered in withdrawal pickers, where any attempt then fails with 'insufficient stock'. The implementation does not match its stated contract.
- **แนวทางแก้:** Add `.Where(i => i.Quantity > 0)` if the picker should only show withdrawable items, or update the doc comment to reflect that all IoT items are returned.

### [WithdrawalService] GetWithdrawalsAsync and GetWithdrawableItemsAsync have no permission gate
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/WithdrawalService.cs:27-47`
- **รายละเอียด:** Both read methods perform no role check, so an anonymous/unauthenticated caller at the service layer can enumerate the full withdrawal history (including who withdrew what, purposes, notes) and all IoT inventory. Write methods are gated but reads are open. If the service is ever invoked from a context where the UI authorization is bypassed, this leaks data. (Write methods RecordWithdrawalAsync/DeleteAsync are correctly gated, so this is consistency/defense-in-depth.)
- **แนวทางแก้:** If reads should be restricted, add an IsInAnyRoleAsync gate (at least an authenticated check) to the read methods consistent with how the rest of the app authorizes data access.


---

## 🔵 INFO

### [CurrentUserAccessor] GetUserAsync silently returns null when the NameIdentifier claim references a deleted/nonexistent user
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/CurrentUserAccessor.cs:21-25`
- **รายละเอียด:** If a principal carries a valid NameIdentifier claim but the corresponding ApplicationUser no longer exists (e.g. deleted while a session is active), GetUserAsync returns null and GetDisplayNameAsync returns "", while GetUserIdAsync still returns the stale id. Downstream code that binds PurchasedById/WithdrawnById/AssignedById to GetUserIdAsync (claim-based, not DB-validated) could create FK references to a user id that no longer exists, depending on FK constraint behavior. Documented here as an edge case; tests assert the current null/empty-string behavior.
- **แนวทางแก้:** If actions must always reference a real user, prefer resolving the id from GetUserAsync (DB-validated) and fail explicitly when the user cannot be found, instead of trusting the claim id alone.

### [DashboardService] Month boundary DateTimes are Kind=Unspecified while Purchase.Date is stored as UtcNow (Kind=Utc)
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/DashboardService.cs:17-20`
- **รายละเอียด:** monthStart/lastMonthStart/seriesStart are built via `new DateTime(year, month, 1)` (Kind=Unspecified) and compared against Purchase.Date which is typically DateTime.UtcNow (Kind=Utc). With Npgsql legacy-timestamp mode the comparison works at the wall-clock level, but the dashboard's notion of 'this month' is implicitly the server's UTC month, not the user's local (Asia/Bangkok) month. Purchases made late on the last day of a Thai month can fall into the previous/next UTC month, shifting them between This/Last month buckets and the 6-month series. Worth confirming the intended timezone.
- **แนวทางแก้:** Decide on the intended reporting timezone explicitly (UTC vs local) and compute month boundaries consistently, e.g. convert to the business timezone before bucketing.

### [ItemService] UpdateAsync does not validate the target CategoryId exists or that Type/CostType field combinations are coherent beyond Normalize clearing
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/ItemService.cs:96`
- **รายละเอียด:** UpdateAsync (and CreateAsync) copy CategoryId directly with no existence check; an invalid CategoryId would fail only via the FK constraint at SaveChanges, surfacing as an unhandled DbUpdateException rather than a friendly OperationResult.Fail. Not exercised in tests because the helper always supplies a valid category, but worth noting as a robustness gap for callers that could pass an arbitrary CategoryId.
- **แนวทางแก้:** Optionally validate CategoryId existence and return a clean OperationResult.Fail, or catch DbUpdateException around SaveChanges to translate FK violations into user-facing errors.

### [PureHelpers] AppRoles.DisplayName is case-sensitive and echoes unknown roles verbatim
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Data/AppRoles.cs:20`
- **รายละเอียด:** DisplayName matches the exact uppercase role constants; a lowercase 'admin' or any unrecognized string is returned unchanged (no localization). Since Identity role comparisons elsewhere are typically case-insensitive, a role string with different casing would display the raw token to the user instead of the Thai label. Asserted as actual behavior in the tests.
- **แนวทางแก้:** If role casing can vary, normalize before switching (e.g. role.ToUpperInvariant()) so the Thai label is still returned.

### [SubscriptionService] GetSubscriptionsAsync, GetUpcomingRenewalsAsync and MonthlyTotal perform no permission check
- **ตำแหน่ง:** `/home/assani-indraprasitdhi/elevenx/elevenx-warehouse/Services/SubscriptionService.cs:26-54`
- **รายละเอียด:** The three read/aggregate methods are not gated by CanManageAsync (only the mutating methods are). An anonymous/no-role accessor can read full subscription data including RecurringAmount via GetSubscriptionsAsync. This appears intentional (reads are open) but is worth confirming against the app's authorization model, since subscription cost data may be considered sensitive. Captured by GetSubscriptions_requires_no_permission_works_for_anonymous.
- **แนวทางแก้:** If subscription/cost reads should be restricted, add a role check to the read methods; otherwise document that reads are intentionally unauthenticated.
