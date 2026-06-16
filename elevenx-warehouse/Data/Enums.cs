namespace ElevenX.Warehouse.Data;

/// <summary>ประเภทของรายการ — แยกพฤติกรรมระหว่างของจับต้องได้ (IoT) กับสินทรัพย์ดิจิทัล (Server/Software)</summary>
public enum ItemType
{
    IotMaterial = 0,
    Server = 1,
    Software = 2,
    Other = 3,
}

/// <summary>รูปแบบค่าใช้จ่าย: จ่ายครั้งเดียว หรือ จ่ายเป็นรอบ (subscription)</summary>
public enum CostType
{
    OneTime = 0,
    Recurring = 1,
}

/// <summary>รอบการเรียกเก็บเงินของ subscription</summary>
public enum BillingCycle
{
    Monthly = 0,
    Quarterly = 1,
    Yearly = 2,
}

/// <summary>สถานะของ subscription</summary>
public enum SubscriptionStatus
{
    Active = 0,
    Cancelled = 1,
    Expired = 2,
}
