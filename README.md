# ElevenX Warehouse — ระบบจัดการสต็อกวัสดุ & ค่าใช้จ่ายภายในบริษัท

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Blazor](https://img.shields.io/badge/Blazor-Interactive%20Server-512BD4?logo=blazor&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-14%2B-4169E1?logo=postgresql&logoColor=white)
![Tailwind CSS](https://img.shields.io/badge/Tailwind-v4-38BDF8?logo=tailwindcss&logoColor=white)
![Tests](https://img.shields.io/badge/tests-630%20passing-16A34A)

Web application สำหรับจัดการ **วัสดุ IoT** (ของจับต้องได้ มีสต็อก เบิกใช้ได้), **Server** และ **Software**
(เก็บค่าใช้จ่าย + นับ license/seat) — บันทึกว่าใครเบิก ใครซื้อ ใครใช้ license และใช้จ่ายรวมเท่าไหร่
รองรับค่าใช้จ่ายทั้งแบบ **จ่ายครั้งเดียว (one-time)** และ **subscription (รายเดือน / ราย 3 เดือน / รายปี)**
พร้อม Authentication แยกตาม Role

---

## ✨ ฟีเจอร์เด่น

- 📊 **Dashboard** — มูลค่าสต็อก, ค่าใช้จ่ายเดือนนี้, subscription ใกล้ครบรอบ, สต็อกต่ำ, seat ใกล้เต็ม + กราฟค่าใช้จ่ายรายเดือน
- 📦 **จัดการ Item** ครบ 3 ประเภท (IoT / Server / Software) — ฟอร์มปรับตามประเภทอัตโนมัติ
- 🛒 **ค่าใช้จ่าย / การซื้อ** + 🔽 **เบิกของ** (ตัดสต็อกอัตโนมัติ) + 🔑 **License / Seat** (จ่าย/คืน)
- 🔄 **Subscription** — รอบบิลถัดไป, บันทึกค่ารอบ, ต่ออายุ, ยกเลิก
- 📑 **รายงาน** ค่าใช้จ่าย/เบิก/license + **Export Excel & PDF** (ฝังฟอนต์ Kanit รองรับภาษาไทย)
- 👥 **จัดการผู้ใช้** + สิทธิ์ตาม Role (ADMIN / PURCHASER / STAFF / VIEWER)
- 🎨 **UI ธีมสว่าง + เขียวเทอร์ควอยซ์**, หน้า Login ดีไซน์ 3D, **Toast แจ้งเตือนมุมขวาบน**, รองรับ **มือถือ** (sidebar เป็น drawer)

---

## 🗂️ โครงสร้าง Repo

```
.
├── elevenx-warehouse/          # แอปหลัก (.NET 10 Blazor Web App)
│   ├── Data/                   # EF Core entities, DbContext, enums, DbSeeder, Migrations
│   ├── Services/               # Business logic ทั้งหมด (inject ผ่าน interface)
│   ├── Components/             # Layout / Shared / Pages / Account (Identity)
│   ├── app.tailwind.css        # Tailwind input (design tokens / theme / components)
│   └── wwwroot/                # static assets + app.css (Tailwind ที่ build แล้ว) + ฟอนต์ Kanit
│
└── ElevenX.Warehouse.Tests/    # ชุดเทสต์ xUnit (630 tests) บน PostgreSQL จริง ผ่าน Testcontainers
```

> 📖 รายละเอียดเชิงลึก: [`elevenx-warehouse/README.md`](elevenx-warehouse/README.md) · [`ElevenX.Warehouse.Tests/README.md`](ElevenX.Warehouse.Tests/README.md)

---

## 🧱 Tech Stack

| ส่วน | เทคโนโลยี |
|------|-----------|
| Framework | .NET 10 (LTS) + C# 14 |
| UI / Backend | **Blazor Web App** — Interactive Server (โปรเจกต์เดียวจบ) |
| Database | PostgreSQL + EF Core 10 (`Npgsql`) |
| Auth | ASP.NET Core Identity + role-based authorization |
| Styling | Tailwind CSS v4 (CLI) + ฟอนต์ Kanit — ธีมสว่าง/เขียวเทอร์ควอยซ์ |
| Charts | ApexCharts (`Blazor-ApexCharts`) |
| Export | Excel (`ClosedXML`) + PDF (`QuestPDF`) |
| Testing | xUnit + Testcontainers (PostgreSQL จริง) |

---

## 🚀 Quick Start

> ต้องมี: **.NET 10 SDK**, **Node.js ≥ 20**, **Docker** (หรือ PostgreSQL ≥ 14 บนเครื่อง)

```bash
# 1) Clone
git clone https://github.com/AssaniIndraprasitdhi/warehouse.git
cd warehouse/elevenx-warehouse

# 2) ฐานข้อมูล PostgreSQL (Docker)
docker run -d --name elevenx-postgres \
  -e POSTGRES_USER=elevenx -e POSTGRES_PASSWORD=elevenx -e POSTGRES_DB=elevenx \
  -p 127.0.0.1:5432:5432 postgres:16
docker exec elevenx-postgres createdb -U elevenx elevenx_warehouse

# 3) ตั้งค่า config (ไฟล์จริงถูก gitignore ไว้ — คัดลอกจาก .example)
cp appsettings.json.example appsettings.json
cp appsettings.Development.json.example appsettings.Development.json
#   แล้วแก้ Username/Password ใน connection string ให้ตรงกับ DB ของคุณ

# 4) build CSS แล้วรัน (migrate + seed ข้อมูลตัวอย่างให้อัตโนมัติตอน startup)
npm install && npm run build:css
dotnet run
```

เปิดเบราว์เซอร์ที่ URL ใน console (เช่น `http://localhost:5033`)

> 💡 ระหว่างพัฒนา: รัน `npm run watch:css` ค้างไว้คู่กับ `dotnet run` เพื่อให้ CSS อัปเดตอัตโนมัติ

### 👥 บัญชีทดสอบ (seed อัตโนมัติ) — รหัสผ่านทุกบัญชี `Passw0rd!`

| Role | อีเมล | สิทธิ์ |
|------|-------|--------|
| ADMIN | `admin@elevenx.local` | ทุกหน้า + จัดการผู้ใช้ |
| PURCHASER | `purchaser@elevenx.local` | จัดการ item / ซื้อ / subscription / license + เบิก + ดูรายงาน |
| STAFF | `staff@elevenx.local` | ดูสต็อก/ค่าใช้จ่าย + บันทึกการเบิก |
| VIEWER | `viewer@elevenx.local` | ดูอย่างเดียว |

---

## 🧪 Testing

ชุดเทสต์ **630 test** ครอบคลุมทุกฟังก์ชัน public ของ Service layer รันบน PostgreSQL จริงผ่าน Testcontainers
(ต้องมี **Docker** ทำงานอยู่ — เทสต์ spin up `postgres:16` เอง ไม่แตะ DB dev/prod):

```bash
cd ElevenX.Warehouse.Tests
dotnet test
```

> ผล audit ระหว่างเขียนเทสต์อยู่ใน [`ElevenX.Warehouse.Tests/AUDIT_FINDINGS.md`](ElevenX.Warehouse.Tests/AUDIT_FINDINGS.md)

---

## 📄 หน้าจอหลัก (Routes)

`/login` · `/dashboard` · `/items` · `/purchases` · `/withdrawals` · `/licenses` · `/subscriptions` · `/reports` · `/users` (ADMIN)

รายละเอียดแต่ละหน้า + คำสั่ง EF migration ดูที่ [`elevenx-warehouse/README.md`](elevenx-warehouse/README.md)

---

## ⚙️ หมายเหตุ config / ความปลอดภัย

- `appsettings.json` และ `appsettings.Development.json` **ถูก gitignore** (มี connection string จริง) — ใช้ไฟล์ `*.example` เป็นต้นแบบ
- อย่า commit credentials ลง repo (repo นี้เป็น **Public**)
