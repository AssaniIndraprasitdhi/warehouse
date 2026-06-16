# Deploy — ElevenX Warehouse → Railway + Neon + Cloudflare (elevenx.tech)

แอปนี้เป็น **.NET 10 Blazor Server (Interactive Server)** ต้องรันบนเครื่องที่มี .NET runtime
ตลอดเวลา + รองรับ WebSocket — **deploy เป็น static ไม่ได้** (Cloudflare Pages/Workers รันไม่ได้)

```
ผู้ใช้ ──HTTPS──> Cloudflare (DNS ของ elevenx.tech) ──> Railway (รัน Docker container) ──> Neon (PostgreSQL)
```

| ส่วน | บริการ | หน้าที่ |
|------|--------|---------|
| Compute | **Railway** | รัน container (Dockerfile ในรีโปนี้), ต่อ WebSocket, ออก TLS |
| Database | **Neon** | PostgreSQL (managed, free tier) |
| DNS/Domain | **Cloudflare** | ชี้ `elevenx.tech` มาที่ Railway |

> ไฟล์ที่เกี่ยวกับ deploy ในรีโป: [`Dockerfile`](Dockerfile), [`.dockerignore`](.dockerignore), [`railway.json`](railway.json)
> ตัว Dockerfile build Tailwind → `dotnet publish` → runtime ครบในตัว (ทดสอบ build + รันจริงแล้ว)

---

## 0) push โค้ดขึ้น GitHub ก่อน

Railway deploy จาก GitHub repo (`AssaniIndraprasitdhi/warehouse`) — ต้อง commit ไฟล์ใหม่/แก้ไขทั้งหมดก่อน
(`Dockerfile`, `.dockerignore`, `railway.json`, `Program.cs`, `DbSeeder.cs`, `ApplicationDbContext.cs`,
csproj, และ migration `AddDataProtectionKeys`) แล้ว `git push`

---

## 1) Neon — สร้าง PostgreSQL

1. ไป https://neon.tech → สมัคร/ล็อกอิน → **Create project** (เลือก region ใกล้ที่สุด เช่น Singapore `ap-southeast-1`)
2. หน้า **Connection string** → คัดลอกแบบ **Pooled connection** (host จะมี `-pooler`)
   จะได้รูปแบบ URI ของ Neon เช่น:
   ```
   postgresql://neondb_owner:npg_xxxxx@ep-cool-name-pooler.ap-southeast-1.aws.neon.tech/neondb?sslmode=require
   ```
3. **แปลงเป็นรูปแบบ Npgsql** (แอปใช้ key=value ไม่ใช่ URI) — เอาไปใส่เป็น env บน Railway:
   ```
   Host=ep-cool-name-pooler.ap-southeast-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_xxxxx;SSL Mode=Require;Trust Server Certificate=true
   ```
   - ใช้ host ที่มี `-pooler` (แอปเปิด connection สั้น ๆ จำนวนมากผ่าน `IDbContextFactory`)
   - `SSL Mode=Require` จำเป็นสำหรับ Neon

> migration + สร้าง schema จะรันอัตโนมัติตอน container บูต ([Program.cs](elevenx-warehouse/Program.cs)) — ไม่ต้องรัน migration เอง

---

## 2) Railway — รันแอป

1. ไป https://railway.com → **New Project** → **Deploy from GitHub repo** → เลือก repo `warehouse`
2. Railway จะเจอ [`railway.json`](railway.json) + [`Dockerfile`](Dockerfile) เอง (builder = DOCKERFILE, healthcheck = `/healthz`)
3. ไปแท็บ **Variables** ของ service → เพิ่ม env (⚠️ ตั้ง **ก่อน** deploy ครั้งแรก):

   | Key | Value | หมายเหตุ |
   |-----|-------|----------|
   | `ConnectionStrings__DefaultConnection` | (Npgsql string จากขั้นที่ 1) | **จำเป็น** |
   | `Seed__AdminPassword` | รหัสผ่าน admin ที่ตั้งเอง (เดายาก) | **จำเป็น** — ถ้าไม่ตั้งจะใช้รหัสสาธารณะ `Passw0rd!` |
   | `Seed__AdminEmail` | เช่น `itdepartment@carpetmaker.co.th` | ไม่บังคับ (ดีฟอลต์ `admin@elevenx.local`) |

   - **ห้ามตั้ง** `PORT` / `ASPNETCORE_URLS` เอง — Railway ใส่ `PORT` ให้ แอปอ่านเองแล้ว
   - `ASPNETCORE_ENVIRONMENT=Production` ตั้งไว้ใน Dockerfile แล้ว
4. กด **Deploy** → ดู log จนเห็น `Now listening on: http://0.0.0.0:<port>` และ healthcheck เขียว
   (log แรกจะมี `Failed executing DbCommand ... __EFMigrationsHistory` 1 ครั้ง = ปกติ เพราะ DB ยังว่าง)

> ⚠️ `Seed__AdminPassword` มีผลเฉพาะ**ตอนสร้าง admin ครั้งแรก** — ถ้า deploy ไปแล้วด้วยรหัสดีฟอลต์
> การมาตั้ง env ทีหลังจะไม่เปลี่ยนรหัสเดิม ให้เข้าไปเปลี่ยนรหัสในแอปแทน (หรือลบ user แล้ว redeploy)

---

## 3) Cloudflare — ชี้ elevenx.tech มาที่ Railway

1. **Railway** → service → **Settings → Networking → Custom Domain** → ใส่ `elevenx.tech` (และ `www.elevenx.tech` ถ้าต้องการ)
   Railway จะให้ค่า **CNAME target** (เช่น `xxxx.up.railway.app`)
2. **Cloudflare** → โดเมน `elevenx.tech` → **DNS → Records** → เพิ่ม:
   | Type | Name | Target | Proxy |
   |------|------|--------|-------|
   | CNAME | `@` (= elevenx.tech) | ค่าจาก Railway | **DNS only (เมฆเทา)** ก่อน |
   | CNAME | `www` | ค่าจาก Railway | DNS only |
   (Cloudflare ทำ CNAME flattening ที่ apex ให้อัตโนมัติ)
3. รอให้ Railway ขึ้นสถานะ domain เป็น active + ออก TLS cert ให้เรียบร้อย → เปิดเว็บผ่าน `https://elevenx.tech` ได้เลย

### (ตัวเลือก) เปิด proxy ของ Cloudflare
ถ้าต้องการ CDN/WAF ของ Cloudflare ค่อยสลับเมฆเป็น **สีส้ม (Proxied)** หลัง cert ของ Railway ออกแล้ว แล้ว:
- **SSL/TLS → Overview → ตั้งเป็น `Full (strict)`** ⚠️ **ห้ามใช้ `Flexible`** (จะเกิด redirect loop เพราะแอปบังคับ HTTPS)
- **SSL/TLS → Edge Certificates → เปิด `Always Use HTTPS`**
- **Network → เปิด `WebSockets`** (เปิดเป็นค่าเริ่มต้นอยู่แล้ว — Blazor Server ต้องใช้)

> ถ้าไม่อยากยุ่งกับ SSL mode: ปล่อยเมฆเทา (DNS only) ไว้ก็พอ — Railway จัดการ HTTPS ครบ Cloudflare เป็นแค่ DNS

---

## 4) หลัง deploy เสร็จ

1. เข้า `https://elevenx.tech/login` → ล็อกอินด้วย `Seed__AdminEmail` + `Seed__AdminPassword`
2. แนะนำ: เปลี่ยนรหัส admin ในแอป + สร้างผู้ใช้จริงในหน้า **ผู้ใช้งาน** แล้วลดการพึ่ง seed account

---

## Troubleshooting

| อาการ | สาเหตุ / วิธีแก้ |
|-------|------------------|
| เปิดเว็บแล้ว redirect วนไม่จบ | Cloudflare SSL เป็น `Flexible` → เปลี่ยนเป็น `Full (strict)` |
| ล็อกอินแล้วหลุดทุกครั้งที่ deploy | ปกติแล้วแก้ไว้ (DP keys เก็บใน DB) — ตรวจว่าตาราง `DataProtectionKeys` มี row และใช้ Neon เดิม (ไม่เปลี่ยน DB) |
| ต่อ DB ไม่ได้ / SSL error | ตรวจ connection string เป็นรูปแบบ Npgsql (key=value) + มี `SSL Mode=Require;Trust Server Certificate=true` |
| UI ไม่ตอบสนอง/ปุ่มกดไม่ติด | WebSocket ถูกบล็อก — ถ้าใช้ Cloudflare proxy ให้เปิด WebSockets ในแท็บ Network |
| ครั้งแรกหลังไม่มีคนใช้นานโหลดช้า | Neon free tier auto-suspend — query แรกปลุก DB (~ครึ่งวินาที) ถือว่าปกติ |

---

## Redeploy
push ขึ้น branch `main` บน GitHub → Railway build + deploy ใหม่อัตโนมัติ
migration ใหม่ (ถ้ามี) จะรันตอนบูตให้เอง
