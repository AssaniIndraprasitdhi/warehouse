namespace ElevenX.Warehouse.Data;

/// <summary>ชื่อ Role ของระบบ (ตรงกับ Identity Roles)</summary>
public static class AppRoles
{
    public const string Admin = "ADMIN";
    public const string Purchaser = "PURCHASER";
    public const string Staff = "STAFF";
    public const string Viewer = "VIEWER";

    public static readonly string[] All = [Admin, Purchaser, Staff, Viewer];

    /// <summary>Role ที่แก้ไขข้อมูล item/purchase/license/subscription ได้</summary>
    public const string CanManage = Admin + "," + Purchaser;

    /// <summary>Role ที่บันทึกการเบิกได้ (รวม STAFF)</summary>
    public const string CanWithdraw = Admin + "," + Purchaser + "," + Staff;

    /// <summary>ป้ายชื่อภาษาไทยของแต่ละ role สำหรับแสดงผล</summary>
    public static string DisplayName(string role) => role switch
    {
        Admin => "ผู้ดูแลระบบ",
        Purchaser => "ฝ่ายจัดซื้อ",
        Staff => "พนักงาน",
        Viewer => "ผู้ชม",
        _ => role,
    };
}
