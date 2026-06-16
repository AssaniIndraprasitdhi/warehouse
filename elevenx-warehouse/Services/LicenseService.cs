using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface ILicenseService
{
    Task<List<LicenseAssignment>> GetAssignmentsAsync(int? itemId = null, bool activeOnly = false, string? search = null);
    Task<Dictionary<int, int>> GetUsedSeatsMapAsync();
    Task<int> GetUsedSeatsAsync(int itemId);
    Task<List<Item>> GetSeatItemsAsync();
    Task<List<SeatUsage>> GetSeatUsageAsync();
    Task<OperationResult<LicenseAssignment>> AssignSeatAsync(int itemId, string assignedToId, string assignedById, string? note);
    Task<OperationResult> ReleaseSeatAsync(int assignmentId);
}

public class LicenseService(IDbContextFactory<ApplicationDbContext> dbFactory, CurrentUserAccessor currentUser) : ILicenseService
{
    private async Task<bool> CanManageAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser);
    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    public async Task<List<LicenseAssignment>> GetAssignmentsAsync(int? itemId = null, bool activeOnly = false, string? search = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.LicenseAssignments
            .Include(l => l.Item)
            .Include(l => l.AssignedTo)
            .AsQueryable();

        if (itemId is not null) q = q.Where(l => l.ItemId == itemId);
        if (activeOnly) q = q.Where(l => l.ReleasedAt == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(l => EF.Functions.ILike(l.Item.Name, pattern)
                          || EF.Functions.ILike(l.AssignedTo.FullName, pattern)
                          || (l.AssignedTo.Email != null && EF.Functions.ILike(l.AssignedTo.Email, pattern)));
        }

        return await q.OrderByDescending(l => l.ReleasedAt == null)
                      .ThenByDescending(l => l.AssignedAt)
                      .ToListAsync();
    }

    public async Task<Dictionary<int, int>> GetUsedSeatsMapAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LicenseAssignments
            .Where(l => l.ReleasedAt == null)
            .GroupBy(l => l.ItemId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<int> GetUsedSeatsAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LicenseAssignments.CountAsync(l => l.ItemId == itemId && l.ReleasedAt == null);
    }

    public async Task<List<Item>> GetSeatItemsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Items
            .Where(i => i.Type == ItemType.Server || i.Type == ItemType.Software)
            .OrderBy(i => i.Name)
            .ToListAsync();
    }

    public async Task<List<SeatUsage>> GetSeatUsageAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var items = await db.Items
            .Where(i => (i.Type == ItemType.Server || i.Type == ItemType.Software) && i.TotalSeats != null)
            .Select(i => new { i.Id, i.Name, i.Type, Total = i.TotalSeats!.Value })
            .ToListAsync();

        var used = await db.LicenseAssignments
            .Where(l => l.ReleasedAt == null)
            .GroupBy(l => l.ItemId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        return items
            .Select(i => new SeatUsage(i.Id, i.Name, i.Type, used.GetValueOrDefault(i.Id), i.Total))
            .OrderByDescending(s => s.UsedRatio)
            .ToList();
    }

    public async Task<OperationResult<LicenseAssignment>> AssignSeatAsync(int itemId, string assignedToId, string assignedById, string? note)
    {
        if (!await CanManageAsync()) return OperationResult<LicenseAssignment>.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null)
            return OperationResult<LicenseAssignment>.Fail("ไม่พบรายการสินค้า");
        if (item.Type is not (ItemType.Server or ItemType.Software))
            return OperationResult<LicenseAssignment>.Fail("จ่าย License/Seat ได้เฉพาะ Server หรือ Software");
        if (item.TotalSeats is null or <= 0)
            return OperationResult<LicenseAssignment>.Fail("รายการนี้ยังไม่ได้กำหนดจำนวน License/Seat");

        var used = await db.LicenseAssignments.CountAsync(l => l.ItemId == itemId && l.ReleasedAt == null);
        if (used >= item.TotalSeats.Value)
            return OperationResult<LicenseAssignment>.Fail($"License/Seat เต็มแล้ว ({used}/{item.TotalSeats})");

        var dup = await db.LicenseAssignments.AnyAsync(l => l.ItemId == itemId && l.AssignedToId == assignedToId && l.ReleasedAt == null);
        if (dup)
            return OperationResult<LicenseAssignment>.Fail("ผู้ใช้นี้ได้รับ License/Seat ของรายการนี้อยู่แล้ว");

        var assignment = new LicenseAssignment
        {
            ItemId = itemId,
            AssignedToId = assignedToId,
            AssignedById = assignedById,
            AssignedAt = DateTime.UtcNow,
            Note = note,
        };
        db.LicenseAssignments.Add(assignment);
        item.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // ชน partial unique index (IX_active_seat) — มีการจ่าย seat ซ้ำพร้อมกัน
            return OperationResult<LicenseAssignment>.Fail("ผู้ใช้นี้ได้รับ License/Seat ของรายการนี้อยู่แล้ว");
        }
        return OperationResult<LicenseAssignment>.Ok(assignment);
    }

    public async Task<OperationResult> ReleaseSeatAsync(int assignmentId)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var assignment = await db.LicenseAssignments.FirstOrDefaultAsync(l => l.Id == assignmentId);
        if (assignment is null)
            return OperationResult.Fail("ไม่พบรายการ License");
        if (assignment.ReleasedAt is not null)
            return OperationResult.Fail("License นี้ถูกคืนไปแล้ว");

        assignment.ReleasedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }
}
