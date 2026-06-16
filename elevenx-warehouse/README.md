# ElevenX — ระบบจัดการสต็อกวัสดุ & ค่าใช้จ่ายภายในบริษัท

ระบบ web application สำหรับจัดการ **วัสดุ IoT** (ของจับต้องได้ มีสต็อก เบิกใช้ได้), **Server** และ **Software**
(เก็บค่าใช้จ่าย + นับ license/seat) — บันทึกว่าใครเบิก ใครซื้อ ใครใช้ license และใช้จ่ายรวมไปเท่าไหร่
รองรับค่าใช้จ่ายทั้งแบบ **จ่ายครั้งเดียว (one-time)** และ **แบบ subscription (รายเดือน/ราย 3 เดือน/รายปี)**
พร้อมระบบ Authentication แยก Role

---

## 🧱 Tech Stack

| ส่วน | เทคโนโลยี |
|------|-----------|
| Framework | .NET 10 (LTS) + C# 14 |
| UI / Backend | **Blazor Web App** — Interactive Server render mode (โปรเจกต์เดียวจบ) |
| Database | PostgreSQL |
| ORM | Entity Framework Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` |
| Auth | ASP.NET Core Identity + role-based authorization |
| Styling | Tailwind CSS v4 (CLI) + ฟอนต์ **Kanit** + ธีม dark navy |
| Charts | ApexCharts (`Blazor-ApexCharts`) |
| Export | Excel (`ClosedXML`) + PDF (`QuestPDF` ฝังฟอนต์ Kanit รองรับภาษาไทย) |

---

## 📋 สิ่งที่ต้องติดตั้งก่อน (Prerequisites)

1. **.NET 10 SDK**
   ตรวจสอบด้วย:
   ```bash
   dotnet --version      # ควรขึ้น 10.0.x
   ```
   ถ้ายังไม่มี ติดตั้งได้จาก <https://dotnet.microsoft.com/download/dotnet/10.0>
   หรือ (Linux/macOS ไม่ต้องใช้ sudo):
   ```bash
   curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
   ```

2. **Node.js** (≥ 20) + npm — ใช้ build Tailwind CSS
   ```bash
   node --version
   ```

3. **PostgreSQL** (รันบนเครื่อง local) — เวอร์ชัน 14 ขึ้นไป

---

## 🐘 ตั้งค่า PostgreSQL

แอปนี้ตั้งค่าให้เชื่อมต่อ database ชื่อ **`elevenx_warehouse`** ที่ `127.0.0.1:5432`
ด้วย user/password = `elevenx` / `elevenx`

### วิธี A — ใช้ Docker (แนะนำ)
```bash
docker run -d --name elevenx-postgres \
  -e POSTGRES_USER=elevenx \
  -e POSTGRES_PASSWORD=elevenx \
  -e POSTGRES_DB=elevenx \
  -p 127.0.0.1:5432:5432 postgres:16

# สร้าง database สำหรับแอป .NET
docker exec elevenx-postgres createdb -U elevenx elevenx_warehouse
```

### วิธี B — PostgreSQL ที่ติดตั้งบนเครื่อง
```bash
createdb elevenx_warehouse
# แล้วแก้ connection string ใน appsettings.json ให้ตรงกับ user/password ของคุณ
```

### Connection string
แก้ได้ที่ `appsettings.json` / `appsettings.Development.json`
(มีไฟล์ตัวอย่าง `appsettings.json.example` ให้ดูรูปแบบ):
```json
"ConnectionStrings": {
  "DefaultConnection": "Host=127.0.0.1;Port=5432;Database=elevenx_warehouse;Username=elevenx;Password=elevenx"
}
```

---

## 🚀 การรัน

```bash
# 1) ติดตั้ง dependency ของ Tailwind แล้ว build CSS
npm install
npm run build:css            # หรือ npm run watch:css ระหว่างพัฒนา

# 2) (ทางเลือก) สร้าง schema เอง — แอปจะ migrate ให้อัตโนมัติตอน startup อยู่แล้ว
dotnet ef database update    # ต้องมี dotnet-ef tool: dotnet tool install --global dotnet-ef

# 3) รันแอป
dotnet run
```

เปิดเบราว์เซอร์ที่ URL ที่ขึ้นใน console (เช่น `http://localhost:5033`)

> 🔄 **Migration + Seed อัตโนมัติ:** ตอน startup แอปจะ `Database.Migrate()` และ seed ข้อมูลตัวอย่าง
> (ผู้ใช้ทุก role, หมวดหมู่, ผู้ขาย, item ครบทุกประเภท, การซื้อ/เบิก/license และ subscription) ให้เองครั้งแรก

---

## 👥 บัญชีทดสอบ (Seed)

รหัสผ่านทุกบัญชีคือ **`Passw0rd!`**

| Role | อีเมล | สิทธิ์ |
|------|-------|--------|
| **ADMIN** | `admin@elevenx.local` | ทุกหน้า + จัดการผู้ใช้ |
| **PURCHASER** | `purchaser@elevenx.local` | จัดการ item / ซื้อ / subscription / license + เบิกได้ + ดูรายงาน |
| **STAFF** | `staff@elevenx.local` | ดูสต็อก/ค่าใช้จ่าย + บันทึกการเบิก |
| **VIEWER** | `viewer@elevenx.local` | ดูอย่างเดียวทุกหน้า |

(มีบัญชีพนักงานเพิ่ม `emp1@elevenx.local` … `emp4@elevenx.local` สำหรับทดสอบการจ่าย license)

---

## 🗂️ โครงสร้างโปรเจกต์

```
Data/                 EF Core entities, AppDbContext, enums, DbSeeder, Migrations
Services/             Business logic ทั้งหมด (Item, Purchase, Withdrawal, License,
                      Subscription, Report, Dashboard, User, Export) + DTOs
Components/
  Layout/             Sidebar, TopBar, MainLayout, AuthLayout
  Shared/             Icon, Modal, ConfirmDialog, StatCard, badges, ExportButtons ฯลฯ
  Pages/              login, dashboard, items, purchases, withdrawals,
                      licenses, subscriptions, reports, users
  Account/            โครงสร้าง ASP.NET Core Identity (จาก template)
app.tailwind.css      Tailwind input (design tokens / palette / component classes)
wwwroot/app.css       CSS ที่ build แล้ว (output ของ Tailwind)
wwwroot/fonts/        ฟอนต์ Kanit (.ttf) สำหรับ export PDF ภาษาไทย
```

### สถาปัตยกรรม
- แยกเป็น layers ชัดเจน: `Data/` → `Services/` → `Components/`
- **Business logic อยู่ใน service class เท่านั้น** (inject เข้า component ผ่าน interface)
- ใช้ `IDbContextFactory<AppDbContext>` (เหมาะกับ Blazor Server) — แต่ละ service สร้าง context อายุสั้น
- ทุก action ผูกกับผู้ใช้ที่ login (ดึง user id จาก `AuthenticationStateProvider` อัตโนมัติ)
- สิทธิ์ตรวจทั้งฝั่ง UI (`AuthorizeView`) และฝั่ง server (`[Authorize(Roles=…)]` + guard ใน method)

---

## 📄 หน้าจอ (Routes)

| Route | คำอธิบาย |
|-------|----------|
| `/login` | เข้าสู่ระบบ (มี loading state) |
| `/dashboard` | สรุปภาพรวม: มูลค่าสต็อก, ค่าใช้จ่ายเดือนนี้, subscription รวม/เดือน, ใกล้ครบรอบ  30 วัน, สต็อกต่ำ, seat ใกล้เต็ม, กราฟค่าใช้จ่ายรายเดือนแยกประเภท, ความเคลื่อนไหวล่าสุด |
| `/items` | รายการ item ทั้งหมด + กรองตาม Type/หมวดหมู่ + ค้นหา + เพิ่ม/แก้ไข (ฟอร์มปรับตาม Type) |
| `/purchases` | log ค่าใช้จ่ายทั้งหมด + บันทึกการซื้อ + บันทึกค่ารอบ subscription |
| `/withdrawals` | เบิกของ (เฉพาะ IoT) — ตัดสต็อก + ตรวจคงเหลือ |
| `/licenses` | จ่าย/คืน seat ของ Server/Software + ดู used/total |
| `/subscriptions` | subscription ที่ active, รอบบิลถัดไป, บันทึกค่ารอบ/ต่ออายุ/ยกเลิก |
| `/reports` | รายงานค่าใช้จ่าย (ตามช่วงเวลา/ประเภท/ผู้ซื้อ/ผู้ขาย/เดือน + one-time vs recurring) + รายงานเบิก + การใช้ license — Export Excel/PDF |
| `/users` | จัดการผู้ใช้ (ADMIN เท่านั้น) |

---

## 🛠️ คำสั่งที่มีประโยชน์

```bash
npm run build:css                     # build Tailwind (production, minified)
npm run watch:css                     # build Tailwind แบบ watch ระหว่างพัฒนา
dotnet build                          # คอมไพล์
dotnet run                            # รันแอป
dotnet ef migrations add <Name>       # เพิ่ม migration ใหม่
dotnet ef database update             # apply migration
```

> ระหว่างพัฒนา ให้รัน `npm run watch:css` ค้างไว้คู่กับ `dotnet run` เพื่อให้ CSS อัปเดตอัตโนมัติ
