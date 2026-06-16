using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Data;

/// <summary>สร้าง roles, ผู้ใช้ตัวอย่างทุก role และข้อมูลตัวอย่างครบทุกประเภท item ตอน startup</summary>
public static class DbSeeder
{
    public const string DefaultPassword = "Passw0rd!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        await using var db = await dbFactory.CreateDbContextAsync();

        // 1) Roles
        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        // 2) ผู้ใช้ประจำ role
        var admin = await EnsureUser(userManager, "admin@elevenx.local", "อนันต์ ผู้ดูแลระบบ", AppRoles.Admin);
        var purchaser = await EnsureUser(userManager, "purchaser@elevenx.local", "ปิยะ ฝ่ายจัดซื้อ", AppRoles.Purchaser);
        var staff = await EnsureUser(userManager, "staff@elevenx.local", "สมชาย พนักงานคลัง", AppRoles.Staff);
        var viewer = await EnsureUser(userManager, "viewer@elevenx.local", "วิชัย ผู้ชมรายงาน", AppRoles.Viewer);
        var emp1 = await EnsureUser(userManager, "emp1@elevenx.local", "กนกพร ทองดี", AppRoles.Viewer);
        var emp2 = await EnsureUser(userManager, "emp2@elevenx.local", "ธนวัฒน์ ศรีสุข", AppRoles.Viewer);
        var emp3 = await EnsureUser(userManager, "emp3@elevenx.local", "ณัฐริกา ใจงาม", AppRoles.Viewer);
        var emp4 = await EnsureUser(userManager, "emp4@elevenx.local", "พีรพล อินทร์แก้ว", AppRoles.Viewer);

        // ถ้ามี item แล้ว = seed domain data ไปแล้ว ไม่ต้องทำซ้ำ
        if (await db.Items.AnyAsync())
            return;

        var now = DateTime.UtcNow;
        var today = now.Date;
        DateTime M(int monthsAgo) => new DateTime(now.Year, now.Month, 1).AddMonths(-monthsAgo).AddDays(9);

        // 3) Categories
        var catSensor = new Category { Name = "เซ็นเซอร์ & โมดูล" };
        var catNetwork = new Category { Name = "อุปกรณ์เครือข่าย" };
        var catServer = new Category { Name = "เซิร์ฟเวอร์" };
        var catSoftware = new Category { Name = "ซอฟต์แวร์ & License" };
        var catConsumable = new Category { Name = "วัสดุสิ้นเปลือง" };
        db.Categories.AddRange(catSensor, catNetwork, catServer, catSoftware, catConsumable);

        // 4) Suppliers
        var supIot = new Supplier { Name = "บริษัท ไอโอที ซัพพลาย จำกัด", Contact = "02-111-2222" };
        var supGravitech = new Supplier { Name = "Gravitech Thailand", Contact = "sales@gravitech.co.th" };
        var supCloud = new Supplier { Name = "Cloud & Server Co.", Contact = "099-888-7777" };
        var supSwLicense = new Supplier { Name = "Software License Hub", Contact = "support@swlicense.co.th" };
        db.Suppliers.AddRange(supIot, supGravitech, supCloud, supSwLicense);

        await db.SaveChangesAsync();

        // 5) Items — IoT materials
        Item Iot(string name, string sku, Category cat, int qty, int min, string unit, string loc) => new()
        {
            Name = name, Sku = sku, Category = cat, Type = ItemType.IotMaterial, CostType = CostType.OneTime,
            Quantity = qty, MinQuantity = min, Unit = unit, Location = loc, CreatedAt = M(5), UpdatedAt = now,
        };

        var dht22 = Iot("เซ็นเซอร์อุณหภูมิ DHT22", "IOT-DHT22", catSensor, 140, 30, "ตัว", "คลัง A-1");
        var esp32 = Iot("โมดูล ESP32 DevKit V1", "IOT-ESP32", catSensor, 12, 25, "ตัว", "คลัง A-2"); // low
        var rpi4 = Iot("Raspberry Pi 4 (4GB)", "IOT-RPI4-4G", catSensor, 6, 10, "ตัว", "คลัง A-3");   // low
        var jumper = Iot("สาย Jumper (ชุด 40 เส้น)", "IOT-JUMP40", catConsumable, 80, 20, "ชุด", "คลัง A-4");
        var lan = Iot("สาย LAN Cat6 (ม้วน 305m)", "NET-CAT6-305", catNetwork, 15, 5, "ม้วน", "คลัง B-1");
        var psu = Iot("Power Supply 5V/3A", "IOT-PSU-5V3A", catConsumable, 45, 15, "ตัว", "คลัง A-5");
        var lora = Iot("Gateway LoRa SX1302", "NET-LORA-SX1302", catNetwork, 4, 6, "ตัว", "คลัง B-2");   // low

        // Server
        var dell = new Item
        {
            Name = "Dell PowerEdge R750 Server", Sku = "SRV-DELL-R750", Category = catServer,
            Type = ItemType.Server, CostType = CostType.OneTime, TotalSeats = 16, Note = "เซิร์ฟเวอร์ on-premise สำหรับ VM",
            CreatedAt = M(3), UpdatedAt = now,
        };
        var awsEc2 = new Item
        {
            Name = "AWS EC2 Production Cluster", Sku = "SRV-AWS-EC2", Category = catServer,
            Type = ItemType.Server, CostType = CostType.Recurring, TotalSeats = 8,
            RecurringAmount = 42000m, BillingCycle = BillingCycle.Monthly, Status = SubscriptionStatus.Active,
            StartDate = M(6), NextBillingDate = today.AddDays(3), Note = "ค่า cloud server รายเดือน",
            CreatedAt = M(6), UpdatedAt = now,
        };

        // Software (subscription + license/seat)
        var m365 = new Item
        {
            Name = "Microsoft 365 Business Standard", Sku = "SW-M365-STD", Category = catSoftware,
            Type = ItemType.Software, CostType = CostType.Recurring, TotalSeats = 25,
            RecurringAmount = 129000m, BillingCycle = BillingCycle.Yearly, Status = SubscriptionStatus.Active,
            StartDate = M(11), NextBillingDate = today.AddDays(20), CreatedAt = M(11), UpdatedAt = now,
        };
        var adobe = new Item
        {
            Name = "Adobe Creative Cloud All Apps", Sku = "SW-ADOBE-CC", Category = catSoftware,
            Type = ItemType.Software, CostType = CostType.Recurring, TotalSeats = 10,
            RecurringAmount = 18000m, BillingCycle = BillingCycle.Monthly, Status = SubscriptionStatus.Active,
            StartDate = M(8), NextBillingDate = today.AddDays(8), CreatedAt = M(8), UpdatedAt = now,
        };
        var jetbrains = new Item
        {
            Name = "JetBrains All Products Pack", Sku = "SW-JB-ALL", Category = catSoftware,
            Type = ItemType.Software, CostType = CostType.Recurring, TotalSeats = 5,
            RecurringAmount = 95000m, BillingCycle = BillingCycle.Yearly, Status = SubscriptionStatus.Active,
            StartDate = M(2), NextBillingDate = today.AddDays(120), CreatedAt = M(2), UpdatedAt = now,
        };
        var slack = new Item
        {
            Name = "Slack Pro (แผนเดิม)", Sku = "SW-SLACK-PRO", Category = catSoftware,
            Type = ItemType.Software, CostType = CostType.Recurring, TotalSeats = 30,
            RecurringAmount = 21000m, BillingCycle = BillingCycle.Monthly, Status = SubscriptionStatus.Cancelled,
            StartDate = M(10), NextBillingDate = today.AddDays(-5), CreatedAt = M(10), UpdatedAt = now,
        };

        db.Items.AddRange(dht22, esp32, rpi4, jumper, lan, psu, lora, dell, awsEc2, m365, adobe, jetbrains, slack);
        await db.SaveChangesAsync();

        // 6) Purchases — IoT restock (one-time)
        void Buy(Item item, int qty, decimal unit, Supplier sup, DateTime date, string by, bool recurring = false, string? note = null)
        {
            db.Purchases.Add(new Purchase
            {
                ItemId = item.Id, SupplierId = recurring ? null : sup.Id, PurchasedById = by,
                IsRecurringCharge = recurring, Quantity = recurring ? 0 : qty, UnitPrice = unit,
                TotalCost = recurring ? unit : qty * unit, Date = date, Note = note,
                PeriodStart = recurring ? date : null,
                PeriodEnd = recurring ? date.AddMonths(1) : null,
            });
        }

        Buy(dht22, 150, 85m, supGravitech, M(3), purchaser.Id);
        Buy(esp32, 50, 210m, supGravitech, M(4), purchaser.Id);
        Buy(esp32, 20, 205m, supGravitech, M(1), purchaser.Id);
        Buy(rpi4, 20, 1850m, supIot, M(2), purchaser.Id);
        Buy(jumper, 100, 45m, supIot, M(3), purchaser.Id);
        Buy(lan, 20, 1200m, supIot, M(2), purchaser.Id);
        Buy(psu, 60, 120m, supGravitech, M(3), purchaser.Id);
        Buy(lora, 10, 3200m, supIot, M(2), purchaser.Id);
        Buy(dht22, 30, 88m, supGravitech, M(0), purchaser.Id);

        // One-time server
        Buy(dell, 16, 20000m, supCloud, M(2), admin.Id, note: "ติดตั้ง 16 VM slots");

        // Recurring charges (subscription) — สร้างประวัติค่ารอบย้อนหลัง
        Buy(awsEc2, 0, 42000m, supCloud, M(2), admin.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(awsEc2, 0, 42000m, supCloud, M(1), admin.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(awsEc2, 0, 42000m, supCloud, M(0), admin.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(adobe, 0, 18000m, supSwLicense, M(2), purchaser.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(adobe, 0, 18000m, supSwLicense, M(1), purchaser.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(adobe, 0, 18000m, supSwLicense, M(0), purchaser.Id, recurring: true, note: "ค่ารอบ รายเดือน");
        Buy(m365, 0, 129000m, supSwLicense, M(4), admin.Id, recurring: true, note: "ค่ารอบ รายปี");
        Buy(jetbrains, 0, 95000m, supSwLicense, M(1), admin.Id, recurring: true, note: "ค่ารอบ รายปี");

        // 7) Withdrawals (IoT)
        void Take(Item item, int qty, DateTime date, string by, string purpose)
            => db.Withdrawals.Add(new Withdrawal { ItemId = item.Id, WithdrawnById = by, Quantity = qty, WithdrawnAt = date, Purpose = purpose });

        Take(dht22, 10, M(1), staff.Id, "ติดตั้งโปรเจกต์ Smart Farm");
        Take(dht22, 5, M(0), staff.Id, "ทดสอบระบบ");
        Take(esp32, 30, M(2), staff.Id, "โปรเจกต์ Smart Building");
        Take(esp32, 8, M(1), staff.Id, "งานซ่อมบำรุง");
        Take(jumper, 20, M(1), staff.Id, "งานต้นแบบ");
        Take(lan, 5, M(0), staff.Id, "เดินสายห้อง Server");
        Take(rpi4, 14, M(2), purchaser.Id, "ติดตั้ง Edge Gateway");
        Take(psu, 15, M(1), staff.Id, "จ่ายไฟชุดเซ็นเซอร์");

        // 8) License assignments
        void Assign(Item item, ApplicationUser user, string by, DateTime at, DateTime? released = null)
            => db.LicenseAssignments.Add(new LicenseAssignment { ItemId = item.Id, AssignedToId = user.Id, AssignedById = by, AssignedAt = at, ReleasedAt = released });

        // M365: 8/25
        foreach (var u in new[] { admin, purchaser, staff, viewer, emp1, emp2, emp3, emp4 })
            Assign(m365, u, admin.Id, M(10));

        // Adobe: 3 active + 1 released = 3/10
        Assign(adobe, emp1, purchaser.Id, M(7));
        Assign(adobe, emp2, purchaser.Id, M(6));
        Assign(adobe, staff, purchaser.Id, M(5));
        Assign(adobe, emp3, purchaser.Id, M(7), released: M(2));

        // JetBrains: 5/5 (เต็ม → seat ใกล้เต็ม)
        foreach (var u in new[] { purchaser, emp1, emp2, emp3, emp4 })
            Assign(jetbrains, u, admin.Id, M(2));

        // AWS: 2/8
        Assign(awsEc2, admin, admin.Id, M(5));
        Assign(awsEc2, purchaser, admin.Id, M(5));

        // Dell VM slots: 6/16
        foreach (var u in new[] { admin, staff, emp1, emp2, emp3, emp4 })
            Assign(dell, u, admin.Id, M(2));

        await db.SaveChangesAsync();
    }

    private static async Task<ApplicationUser> EnsureUser(UserManager<ApplicationUser> userManager, string email, string fullName, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                CreatedAt = DateTime.UtcNow,
            };
            await userManager.CreateAsync(user, DefaultPassword);
        }
        if (!await userManager.IsInRoleAsync(user, role))
            await userManager.AddToRoleAsync(user, role);
        return user;
    }
}
