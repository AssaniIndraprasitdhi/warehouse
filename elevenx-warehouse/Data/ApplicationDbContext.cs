using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<LicenseAssignment> LicenseAssignments => Set<LicenseAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ---------- Category ----------
        builder.Entity<Category>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
        });

        // ---------- Item ----------
        builder.Entity<Item>(e =>
        {
            // Sku unique เฉพาะค่าที่ไม่เป็น null
            e.HasIndex(i => i.Sku)
                .IsUnique()
                .HasFilter("\"Sku\" IS NOT NULL");

            e.Property(i => i.RecurringAmount).HasColumnType("decimal(12,2)");

            // เก็บ enum เป็น string เพื่ออ่านง่ายใน DB
            e.Property(i => i.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.CostType).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.BillingCycle).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);

            e.HasOne(i => i.Category)
                .WithMany(c => c.Items)
                .HasForeignKey(i => i.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- Purchase ----------
        builder.Entity<Purchase>(e =>
        {
            e.Property(p => p.UnitPrice).HasColumnType("decimal(12,2)");
            e.Property(p => p.TotalCost).HasColumnType("decimal(12,2)");
            e.HasIndex(p => p.Date);

            e.HasOne(p => p.Item)
                .WithMany(i => i.Purchases)
                .HasForeignKey(p => p.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(p => p.Supplier)
                .WithMany(s => s.Purchases)
                .HasForeignKey(p => p.SupplierId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.PurchasedBy)
                .WithMany()
                .HasForeignKey(p => p.PurchasedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- Withdrawal ----------
        builder.Entity<Withdrawal>(e =>
        {
            e.HasIndex(w => w.WithdrawnAt);

            e.HasOne(w => w.Item)
                .WithMany(i => i.Withdrawals)
                .HasForeignKey(w => w.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.WithdrawnBy)
                .WithMany()
                .HasForeignKey(w => w.WithdrawnById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- LicenseAssignment ----------
        builder.Entity<LicenseAssignment>(e =>
        {
            e.HasIndex(l => new { l.ItemId, l.ReleasedAt });

            // กันจ่าย seat ซ้ำให้ผู้ใช้คนเดิมขณะที่ยัง active (ระดับ DB — กัน race condition)
            e.HasIndex(l => new { l.ItemId, l.AssignedToId })
                .IsUnique()
                .HasFilter("\"ReleasedAt\" IS NULL")
                .HasDatabaseName("IX_active_seat");

            e.HasOne(l => l.Item)
                .WithMany(i => i.LicenseAssignments)
                .HasForeignKey(l => l.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.AssignedTo)
                .WithMany()
                .HasForeignKey(l => l.AssignedToId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
