using Xunit;

namespace ElevenX.Warehouse.Tests.Infrastructure;

/// <summary>
/// base class สำหรับ test ที่ต้องใช้ฐานข้อมูล — แต่ละ test method จะได้ <see cref="TestDatabase"/> ใหม่
/// (xUnit สร้าง instance ใหม่ต่อ test เสมอ) จึงเป็นอิสระต่อกันโดยสมบูรณ์
/// </summary>
[Collection(DatabaseCollection.Name)]
public abstract class DatabaseTestBase : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    protected TestDatabase Db = null!;

    protected DatabaseTestBase(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => Db = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await Db.DisposeAsync();
}
