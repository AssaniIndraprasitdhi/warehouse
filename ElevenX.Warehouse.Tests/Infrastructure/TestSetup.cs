using System.Runtime.CompilerServices;

namespace ElevenX.Warehouse.Tests.Infrastructure;

/// <summary>
/// ตั้งค่า global เหมือน Program.cs ก่อน test ทุกตัวจะรัน:
/// - Npgsql legacy timestamp (เก็บ DateTime เป็น "timestamp without time zone")
/// - QuestPDF community license (ใช้ตอนทดสอบ ExportService.ToPdf)
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    public static void Init()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }
}
