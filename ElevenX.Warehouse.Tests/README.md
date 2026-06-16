# ElevenX.Warehouse.Tests

ชุด **audit test ครบทุกฟังก์ชัน** ของ Service layer ในแอป ElevenX.Warehouse
(630 test methods, ผ่านทั้งหมด ✅) รันบน **PostgreSQL จริง** ผ่าน Testcontainers

## รันยังไง

ต้องมี **Docker** ทำงานอยู่ (test จะ spin up คอนเทนเนอร์ `postgres:16` ให้เองอัตโนมัติ — ไม่แตะ DB dev/prod)

```bash
cd ElevenX.Warehouse.Tests
dotnet test
```

- รันเฉพาะบาง service: `dotnet test --filter "FullyQualifiedName~ItemServiceTests"`
- รันเฉพาะ test ตามชื่อ: `dotnet test --filter "FullyQualifiedName~RecordWithdrawal"`
- เวลารันทั้งชุด ~1.5 นาที (แต่ละ test สร้างฐานข้อมูลใหม่ของตัวเอง)

## ทำไมใช้ Postgres จริง (ไม่ใช่ InMemory)

โค้ดใช้ฟีเจอร์เฉพาะ PostgreSQL ที่ InMemory/SQLite จำลองไม่ได้:
- `EF.Functions.ILike(...)` — ค้นหาแบบ case-insensitive (ทุก service ที่มี search)
- partial unique index `IX_active_seat` — กันจ่าย seat ซ้ำระดับ DB (race condition)
- unique filtered index บน `Sku` / cascade & restrict delete behaviors

แต่ละ test จึงทดสอบ **พฤติกรรมจริงที่ production จะเจอ** ไม่ใช่พฤติกรรมจำลอง

## สถาปัตยกรรม test

| ไฟล์ | หน้าที่ |
|---|---|
| `Infrastructure/PostgresFixture.cs` | เริ่ม container `postgres:16` ครั้งเดียวใช้ร่วมทั้ง run |
| `Infrastructure/TestDatabase.cs` | สร้าง DB ใหม่ + schema (`EnsureCreated`) + Identity stack + helper สร้างข้อมูล (builders) ต่อ 1 test |
| `Infrastructure/DatabaseTestBase.cs` | base class: ทุก test method ได้ `Db` (TestDatabase) ใหม่ที่เป็นอิสระต่อกัน |
| `Infrastructure/TestAuthStateProvider.cs` | จำลองผู้ใช้ที่ login (role/identity) เพื่อทดสอบการบังคับสิทธิ์ |
| `Infrastructure/TestSetup.cs` | ตั้ง Npgsql legacy-timestamp + QuestPDF license (เหมือน Program.cs) |

ทุก test ใน collection เดียว → รัน **เรียงตามลำดับ** (ไม่ชนกัน) และแต่ละ test ใช้ DB ของตัวเอง → **ไม่มี state รั่วระหว่าง test**

## Coverage (ครบทุกฟังก์ชัน public ของ Service layer)

| ไฟล์ test | tests | ครอบคลุม |
|---|---:|---|
| `PureHelpersTests.cs` | 77 | BillingMath, DisplayLabels, AppRoles, OperationResult, computed props ของ Item/SeatUsage/MonthlySpendPoint/SpendReportRow/LicenseAssignment |
| `ItemServiceTests.cs` | 86 | CRUD, Normalize, SKU/หมวด/ผู้ขาย, low-stock, สิทธิ์ |
| `SubscriptionServiceTests.cs` | 71 | บันทึกค่ารอบ, cancel/reactivate, upcoming renewals, MonthlyTotal |
| `PurchaseServiceTests.cs` | 63 | บันทึก/ลบการซื้อ + ผลต่อสต็อก/seat, filters, search |
| `LicenseServiceTests.cs` | 55 | จ่าย/คืน seat, seat usage, ที่นั่งเต็ม, จ่ายซ้ำ, search |
| `ReportServiceTests.cs` | 52 | spend report (5 แบบ group), withdrawal report, license usage, purchasers |
| `WithdrawalServiceTests.cs` | 51 | เบิก/ลบ + สต็อก, สิทธิ์ (STAFF เบิกได้/ลบไม่ได้), validation |
| `UserServiceTests.cs` | 43 | จัดการผู้ใช้ผ่าน Identity, กัน admin คนสุดท้าย, lock/reset |
| `DashboardServiceTests.cs` | 39 | aggregate ทุกตัวของ GetSummaryAsync |
| `CurrentUserAccessorTests.cs` | 24 | อ่าน user id/ชื่อ/role จาก auth state |
| `ExportServiceTests.cs` | 18 | สร้าง Excel/PDF, ragged rows, sanitize ชื่อ sheet |
| `SmokeTest.cs` | 3 | ยืนยัน infra (Testcontainers + role gating) |
| **รวม** | **630** | |

## ผลการ audit

ระหว่างเขียน test พบ **58 ข้อสังเกต/บั๊ก** — ดูรายละเอียดใน [`AUDIT_FINDINGS.md`](AUDIT_FINDINGS.md)
(🔴 2 High · 🟠 15 Medium · 🟡 36 Low · 🔵 5 Info)

หลักการ test: **assert พฤติกรรมจริงปัจจุบัน** เพื่อให้ชุด test เป็น regression baseline ที่เขียวเสมอ
ตำแหน่งที่พบบั๊กถูกติดคอมเมนต์ `// AUDIT[severity]: ...` ไว้เหนือ assertion ที่เกี่ยวข้อง
(ค้นด้วย `grep -rn "AUDIT\[" Services/`)
