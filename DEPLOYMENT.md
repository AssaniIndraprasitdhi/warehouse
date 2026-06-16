# Deploy — ElevenX Warehouse → Render + Neon + Cloudflare (elevenx.tech) · ฟรี $0

แอปนี้เป็น **.NET 10 Blazor Server** ต้องรันบนเครื่องที่มี .NET runtime ตลอดเวลา + รองรับ WebSocket
(deploy เป็น static ไม่ได้)

```
ผู้ใช้ ──HTTPS──> Cloudflare (DNS elevenx.tech) ──> Render (รัน Docker) ──> Neon (PostgreSQL)
                                                         ▲
                              GitHub Actions: keep-warm (ปลุกไม่ให้หลับ) + db-backup (สำรองรายวัน)
```

| ส่วน | บริการ | แพ็กเกจ |
|------|--------|---------|
| Compute | **Render** Web Service (Docker) | Free |
| Database | **Neon** PostgreSQL | Free |
| DNS/Domain | **Cloudflare** | Free |

### ⚠️ ข้อจำกัด free tier ที่ต้องรู้
- **Render free หลับเมื่อไม่มีคนใช้ ~15 นาที** → เปิดครั้งถัดไปรอ ~40 วิ (ตื่น) — แก้ด้วย workflow `keep-warm` (ข้อ 4)
- **Neon free แทบไม่มี backup** → ใช้ workflow `db-backup` (ข้อ 5) เป็นตาข่ายกันข้อมูลหาย
- Render free จำกัด **750 ชม./เดือน** — keep-warm ตั้งไว้เฉพาะเวลาทำงาน (จ.-ศ.) จึงไม่เกิน

> ไฟล์ที่เกี่ยวข้องในรีโป: [`Dockerfile`](Dockerfile), [`render.yaml`](render.yaml),
> [`.github/workflows/keep-warm.yml`](.github/workflows/keep-warm.yml), [`.github/workflows/db-backup.yml`](.github/workflows/db-backup.yml)

---

## 0) push โค้ดขึ้น GitHub (ทำแล้ว)
Render/Actions ดึงจาก repo `AssaniIndraprasitdhi/warehouse` branch `main`

> 💡 ข้อมูลการเงินจริง: แนะนำทำ repo เป็น **private** (Settings → General → Change visibility)

---

## 1) Neon — สร้าง PostgreSQL
1. https://neon.tech → สมัคร → **Create project** (region ใกล้สุด เช่น Singapore)
2. หน้า **Connection string** จะมี 2 แบบ — ต้องใช้**ทั้งคู่**คนละที่:

   **(ก) Pooled (host มี `-pooler`)** → ใช้กับแอปบน Render — แปลงเป็นรูปแบบ **Npgsql**:
   ```
   Host=ep-xxx-pooler.ap-southeast-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_xxx;SSL Mode=Require;Trust Server Certificate=true
   ```
   **(ข) Direct (host ไม่มี `-pooler`)** → ใช้กับ backup (pg_dump) — เก็บรูปแบบ **URI เดิม**:
   ```
   postgresql://neondb_owner:npg_xxx@ep-xxx.ap-southeast-1.aws.neon.tech/neondb?sslmode=require
   ```
   (pg_dump ทำงานกับ pooler ไม่ได้ จึงต้องใช้ direct)

> migration + สร้าง schema รันอัตโนมัติตอน container บูต ([Program.cs](elevenx-warehouse/Program.cs))

---

## 2) Render — รันแอป
1. https://render.com → สมัคร (ล็อกอินด้วย GitHub) — ไม่ต้องผูกบัตรสำหรับ free
2. **New → Blueprint** → เลือก repo `warehouse` → Render อ่าน [`render.yaml`](render.yaml) เอง
   (ถ้าไม่อยากใช้ Blueprint: **New → Web Service** → เลือก repo → Runtime = **Docker** → Health Check Path = `/healthz` → Plan = **Free**)
3. ใส่ **Environment Variables** (ค่าที่ `sync:false` Render จะถามตอน apply):

   | Key | Value |
   |-----|-------|
   | `ConnectionStrings__DefaultConnection` | Npgsql string **(ก)** จากข้อ 1 |
   | `Seed__AdminPassword` | รหัส admin ที่ตั้งเอง เช่น `Carpet#Warehouse2026` |
   | `Seed__AdminEmail` | (ไม่บังคับ) เช่น `itdepartment@carpetmaker.co.th` |

   - **อย่าใส่** `PORT` — Render ใส่ให้เอง แอปอ่าน `PORT` แล้ว ([Program.cs](elevenx-warehouse/Program.cs))
4. **Create / Apply** → ดู **Logs** จนเห็น `Now listening on: http://0.0.0.0:<port>` + health เขียว
   (log แรกมี `Failed ... __EFMigrationsHistory` 1 ครั้ง = ปกติ DB ยังว่าง)
5. Render ให้ URL เช่น `https://elevenx-warehouse.onrender.com` → เปิด `/login` → ล็อกอินด้วย admin ทดสอบก่อน

> ⚠️ `Seed__AdminPassword` มีผลเฉพาะตอนสร้าง admin **ครั้งแรก** — ถ้าเผลอ deploy ด้วยรหัสดีฟอลต์ไปแล้ว
> ตั้ง env ทีหลังจะไม่เปลี่ยนรหัสเดิม ให้เปลี่ยนรหัสในแอปแทน

---

## 3) Cloudflare — ชี้ elevenx.tech มาที่ Render
1. **Render** → service → **Settings → Custom Domains → Add** → ใส่ `elevenx.tech` (+ `www`)
   Render จะให้ค่า **CNAME target** (เช่น `elevenx-warehouse.onrender.com`)
2. **Cloudflare** → `elevenx.tech` → **DNS → Records**:
   | Type | Name | Target | Proxy |
   |------|------|--------|-------|
   | CNAME | `@` | ค่าจาก Render | **DNS only (เมฆเทา)** ก่อน |
   | CNAME | `www` | ค่าจาก Render | DNS only |
3. รอ Render verify + ออก TLS cert → เปิด `https://elevenx.tech` ได้
4. (ตัวเลือก) เปิด proxy Cloudflare (เมฆส้ม) เพื่อใช้ CDN/WAF → ต้องตั้ง **SSL/TLS = `Full (strict)`**
   ⚠️ **ห้าม `Flexible`** (จะ redirect วนไม่จบ) + เปิด `Always Use HTTPS` + **Network → WebSockets = ON**

---

## 4) Keep-warm — กัน Render หลับ (workflow มีให้แล้ว)
หลังได้ URL จากข้อ 2/3:
1. GitHub repo → **Settings → Secrets and variables → Actions → Variables → New repository variable**
2. ชื่อ `APP_URL` ค่า = URL ที่ใช้จริง (เช่น `https://elevenx.tech` หรือ `https://elevenx-warehouse.onrender.com`)
3. [`keep-warm`](.github/workflows/keep-warm.yml) จะ ping `/healthz` ทุก 10 นาที ช่วง จ.-ศ. 08:00-18:00 (ICT)
   (ปรับเวลาได้ที่ `cron` ในไฟล์ — เป็น UTC)

---

## 5) Backup — สำรอง DB รายวัน (workflow มีให้แล้ว)
1. GitHub repo → **Settings → Secrets and variables → Actions → Secrets → New repository secret** เพิ่ม 2 ตัว:
   | Secret | ค่า |
   |--------|-----|
   | `NEON_DATABASE_URL` | connection string **(ข) Direct (URI)** จากข้อ 1 |
   | `BACKUP_PASSPHRASE` | รหัสลับสำหรับเข้ารหัส backup (เก็บไว้ดี ๆ — ใช้ตอน restore) |
2. [`db-backup`](.github/workflows/db-backup.yml) รันทุกวัน 02:00 ICT → ได้ไฟล์ `backup.sql.gz.gpg` (เข้ารหัส) เก็บเป็น artifact 90 วัน
   (กดรันเองได้ที่แท็บ **Actions → db-backup → Run workflow**)
3. **วิธี restore** (โหลด artifact มาแล้ว):
   ```bash
   gpg --batch --passphrase "$BACKUP_PASSPHRASE" -d backup.sql.gz.gpg | gunzip | psql "<DIRECT_DATABASE_URL>"
   ```

---

## 6) หลัง deploy
- เปลี่ยนรหัส admin ในแอป + สร้างผู้ใช้จริงในหน้า **ผู้ใช้งาน**
- ตรวจว่า `db-backup` รันผ่าน (แท็บ Actions) อย่างน้อย 1 ครั้ง

---

## Troubleshooting
| อาการ | วิธีแก้ |
|-------|---------|
| เปิดครั้งแรกรอนาน / ปุ่มยังกดไม่ติด | Render กำลังตื่นจากหลับ (~40 วิ) — keep-warm (ข้อ 4) ช่วยลดในเวลาทำงาน |
| redirect วนไม่จบ | Cloudflare SSL เป็น `Flexible` → เปลี่ยนเป็น `Full (strict)` |
| ล็อกอินหลุดหลัง redeploy | ปกติแก้แล้ว (DP keys ใน DB) — เช็คตาราง `DataProtectionKeys` มี row และใช้ Neon เดิม |
| ต่อ DB ไม่ได้ / SSL error | connection string ต้องเป็นรูปแบบ Npgsql + `SSL Mode=Require;Trust Server Certificate=true` |
| backup workflow ล้มเหลว | ตรวจว่า `NEON_DATABASE_URL` เป็นแบบ **direct** (ไม่มี -pooler) |
| UI ช้าตลอด (ไม่ใช่แค่ครั้งแรก) | region ของ Render ไกล — ลองตั้ง `region: singapore` ใน render.yaml |

---

## ทางเลือกอื่น (ถ้าโตขึ้น/อยากเสถียรกว่า)
- **Railway** (~$5/เดือน) — ไม่หลับ ลื่นกว่า; config [`railway.json`](railway.json) มีอยู่แล้ว ใช้ Dockerfile เดิม
- **Fly.io** — always-on ได้แต่ต้องผูกบัตร
- ทุกทางใช้ Dockerfile + workflow backup ชุดเดิมได้หมด
