using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>smoke test ยืนยันว่า infra (Testcontainers + schema + role gating) ทำงานครบ end-to-end</summary>
public class SmokeTest(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public void BillingMath_pure_function_works()
    {
        Assert.Equal(12, BillingMath.MonthsPerCycle(BillingCycle.Yearly));
    }

    [Fact]
    public async Task ItemService_create_then_read_roundtrips_through_real_postgres()
    {
        var cat = await Db.AddCategoryAsync("เซ็นเซอร์");
        var svc = new ItemService(Db.Factory, Db.Accessor(AppRoles.Admin));

        var result = await svc.CreateAsync(new Item
        {
            Name = "DHT22",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
        });

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        var fetched = await svc.GetByIdAsync(result.Value!.Id);
        Assert.NotNull(fetched);
        Assert.Equal("DHT22", fetched!.Name);
    }

    [Fact]
    public async Task ItemService_create_is_forbidden_for_anonymous()
    {
        var cat = await Db.AddCategoryAsync();
        var svc = new ItemService(Db.Factory, Db.Anonymous());

        var result = await svc.CreateAsync(new Item { Name = "x", CategoryId = cat.Id });

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }
}
