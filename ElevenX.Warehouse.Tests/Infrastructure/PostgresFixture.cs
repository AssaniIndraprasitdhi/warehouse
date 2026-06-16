using Testcontainers.PostgreSql;
using Xunit;

namespace ElevenX.Warehouse.Tests.Infrastructure;

/// <summary>
/// เริ่ม PostgreSQL container เดียวใช้ร่วมกันทั้ง test run (เร็ว) — แต่ละ test สร้าง "ฐานข้อมูลใหม่"
/// ของตัวเองผ่าน <see cref="CreateDatabaseAsync"/> เพื่อความเป็นอิสระต่อกันโดยสมบูรณ์ (ไม่แตะ DB dev จริง)
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("postgres")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    /// <summary>connection string ของฐานข้อมูล maintenance (ใช้ CREATE DATABASE)</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>สร้างฐานข้อมูลใหม่ + schema (EnsureCreated) + Identity service provider สำหรับ test หนึ่งตัว</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
        => await TestDatabase.CreateAsync(AdminConnectionString);
}

/// <summary>รวม test ทุกตัวไว้ใน collection เดียว เพื่อให้ใช้ container ร่วมกัน (ไม่สตาร์ตซ้ำ)</summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
