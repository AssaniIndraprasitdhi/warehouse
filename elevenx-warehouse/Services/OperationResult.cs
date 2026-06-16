namespace ElevenX.Warehouse.Services;

/// <summary>ผลลัพธ์ของ action ที่อาจล้มเหลว พร้อมข้อความ error ภาษาไทยสำหรับแสดงบน UI</summary>
public record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

public record OperationResult<T>(bool Success, T? Value = default, string? Error = null)
{
    public static OperationResult<T> Ok(T value) => new(true, value);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
